using ResetMe.Core.Domain;
using ResetMe.Core.Policies;
using ResetMe.Core.Ports;
using ResetMe.Core.Reset;

namespace ResetMe.Core.Monitoring;

public sealed record MonitorTiming(
    TimeSpan PollInterval,
    TimeSpan BlockedPollInterval,
    TimeSpan AuthRetryInterval,
    IReadOnlyList<TimeSpan> ReconnectBackoff)
{
    /// <summary>PRD FR-02 / §27: configured interval, slower while blocked, 1-2-5-10-30s reconnect backoff.</summary>
    public static MonitorTiming FromOptions(GuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var poll = TimeSpan.FromSeconds(Math.Max(MonitorOptions.MinimumIntervalSeconds, options.Monitor.IntervalSeconds));
        var blocked = poll > TimeSpan.FromSeconds(60) ? poll : TimeSpan.FromSeconds(60);
        return new MonitorTiming(
            poll,
            blocked,
            TimeSpan.FromSeconds(60),
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)]);
    }
}

/// <summary>
/// Polls Codex usage (push signals trigger an early read), detects limit episodes and applies the
/// configured mode. Every reset goes through <see cref="ResetManager"/>, so all double-reset
/// protections apply here too.
/// </summary>
public sealed class RateLimitMonitor
{
    private readonly ICodexConnector _connector;
    private readonly Func<ICodexUsageClient, ResetManager> _resetManagerFactory;
    private readonly IResetStateStore _store;
    private readonly IMonitorObserver _observer;
    private readonly GuardOptions _options;
    private readonly MonitorTiming _timing;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _seenEpisodes = [];
    private volatile bool _accountDirty = true;

    public RateLimitMonitor(
        ICodexConnector connector,
        Func<ICodexUsageClient, ResetManager> resetManagerFactory,
        IResetStateStore store,
        IMonitorObserver observer,
        GuardOptions options,
        MonitorTiming timing,
        TimeProvider time)
    {
        _connector = connector;
        _resetManagerFactory = resetManagerFactory;
        _store = store;
        _observer = observer;
        _options = options;
        _timing = timing;
        _time = time;
    }

    /// <summary>Runs until cancelled. Never throws for Codex outages; it reconnects instead.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            ICodexConnection? connection = null;
            try
            {
                connection = await _connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _accountDirty = true;
                await RunConnectedAsync(connection, () => failures = 0, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var backoff = _timing.ReconnectBackoff;
                var delay = backoff.Count == 0 ? TimeSpan.Zero : backoff[Math.Min(failures, backoff.Count - 1)];
                failures++;
                _observer.OnUnavailable(ex, delay);
                if (!await DelayAsync(delay, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            finally
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task RunConnectedAsync(ICodexConnection connection, Action onHealthyRead, CancellationToken cancellationToken)
    {
        using var wake = new SemaphoreSlim(0, 1);
        void Poke()
        {
            try
            {
                wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signalled.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        void OnAccount()
        {
            _accountDirty = true;
            Poke();
        }

        connection.UsageChanged += Poke;
        connection.AccountChanged += OnAccount;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_accountDirty)
                {
                    _accountDirty = false;
                    var account = await connection.GetAccountAsync(cancellationToken).ConfigureAwait(false);
                    if (!account.IsAuthenticated)
                    {
                        _accountDirty = true;
                        _observer.OnAuthRequired();
                        await WaitAsync(wake, _timing.AuthRetryInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                var usage = await connection.GetUsageAsync(includeCreditDetails: false, cancellationToken).ConfigureAwait(false);
                if (LimitEvaluator.IsBlocked(usage))
                {
                    // Credit details (expiry, id) are only needed once a reset is on the table.
                    usage = await connection.GetUsageAsync(includeCreditDetails: true, cancellationToken).ConfigureAwait(false);
                }

                onHealthyRead();
                var assessment = LimitEvaluator.Assess(usage, _options, _time.GetUtcNow());
                _observer.OnUsage(usage, assessment);

                if (assessment.Blocked
                    && await HandleBlockedAsync(connection, usage, assessment, cancellationToken).ConfigureAwait(false))
                {
                    continue; // A reset was attempted: show its effect right away instead of after the blocked interval.
                }

                var interval = assessment.Blocked ? _timing.BlockedPollInterval : _timing.PollInterval;
                await WaitAsync(wake, interval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            connection.UsageChanged -= Poke;
            connection.AccountChanged -= OnAccount;
        }
    }

    /// <summary>Returns true when a reset was attempted.</summary>
    private async Task<bool> HandleBlockedAsync(
        ICodexConnection connection,
        CodexUsage usage,
        LimitAssessment assessment,
        CancellationToken cancellationToken)
    {
        var episode = assessment.LimitEventId!;
        if (!_seenEpisodes.Add(episode))
        {
            return false; // One notice and at most one decision per episode.
        }

        var handling = DecideHandling(assessment, episode);
        var notice = new LimitNotice(usage, assessment, handling);
        _observer.OnLimitReached(notice);

        switch (handling)
        {
            case LimitHandling.AskUser:
                if (!await _observer.ConfirmResetAsync(notice, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                await ResetAsync(connection, automatic: false, cancellationToken).ConfigureAwait(false);
                return true;

            case LimitHandling.ResetAutomatically:
                await ResetAsync(connection, automatic: true, cancellationToken).ConfigureAwait(false);
                return true;

            default:
                return false;
        }
    }

    private LimitHandling DecideHandling(LimitAssessment assessment, string episode)
    {
        if (_store.Load().LastResolvedLimitEventId == episode)
        {
            return LimitHandling.AlreadyHandled;
        }

        if (!assessment.ResetOffered)
        {
            return LimitHandling.NotOffered;
        }

        return _options.Mode switch
        {
            GuardMode.Confirm => LimitHandling.AskUser,
            GuardMode.Automatic => LimitHandling.ResetAutomatically,
            _ => LimitHandling.ReportOnly,
        };
    }

    private async Task ResetAsync(ICodexConnection connection, bool automatic, CancellationToken cancellationToken)
    {
        try
        {
            var report = await _resetManagerFactory(connection)
                .ExecuteAsync(new ResetRequest(Automatic: automatic), cancellationToken)
                .ConfigureAwait(false);
            _observer.OnResetCompleted(report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not retried here: a persisted pending attempt is resumed by an explicit `resetme reset`.
            _observer.OnResetFailed(ex);
        }
    }

    private static async Task WaitAsync(SemaphoreSlim wake, TimeSpan interval, CancellationToken cancellationToken) =>
        await wake.WaitAsync(interval, cancellationToken).ConfigureAwait(false);

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
