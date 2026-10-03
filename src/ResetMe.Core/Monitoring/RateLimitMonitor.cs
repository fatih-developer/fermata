using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResetMe.Core.Domain;
using ResetMe.Core.Logging;
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
            // Quick retries for blips, then slower so a missing or stopped Codex costs almost nothing.
            [.. new[] { 1, 2, 5, 10, 30, 60, 120, 300 }.Select(s => TimeSpan.FromSeconds(s))]);
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
    private readonly ILogger _logger;
    private readonly HashSet<string> _seenEpisodes = [];
    private readonly NearLimitTracker _nearLimit;
    private volatile bool _accountDirty = true;
    private bool _wasBlocked;

    public RateLimitMonitor(
        ICodexConnector connector,
        Func<ICodexUsageClient, ResetManager> resetManagerFactory,
        IResetStateStore store,
        IMonitorObserver observer,
        GuardOptions options,
        MonitorTiming timing,
        TimeProvider time,
        ILogger<RateLimitMonitor>? logger = null)
    {
        _logger = logger ?? NullLogger<RateLimitMonitor>.Instance;
        _nearLimit = new NearLimitTracker(options.NearLimit.Enabled ? options.NearLimit.Thresholds : []);
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
        ReportPendingAttempt();
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            ICodexConnection? connection = null;
            try
            {
                connection = await _connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
                _accountDirty = true;
                MonitorLog.Connected(_logger, _options.Mode.ToString(), _timing.PollInterval.TotalSeconds);
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
                if (failures == 1 || failures % 10 == 0)
                {
                    MonitorLog.Unavailable(_logger, ex, delay.TotalSeconds);
                }
                else
                {
                    MonitorLog.StillUnavailable(_logger, failures, delay.TotalSeconds);
                }

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

    /// <summary>An attempt left unresolved by a crash or an unverifiable result needs the user.</summary>
    private void ReportPendingAttempt()
    {
        try
        {
            if (_store.Load().Pending is { } pending)
            {
                MonitorLog.PendingAttempt(_logger, pending.IdempotencyKey, pending.StartedAt);
                _observer.OnPendingAttempt(pending);
            }
        }
        catch (InvalidDataException ex)
        {
            MonitorLog.ResetFailed(_logger, ex);
            _observer.OnResetFailed(ex);
        }
    }

    private async Task RunConnectedAsync(ICodexConnection connection, Action onHealthyRead, CancellationToken cancellationToken)
    {
        using var wake = new SemaphoreSlim(0, 1);
        void OnUsageSignal()
        {
            MonitorLog.Push(_logger, "usage");
            Poke();
        }

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
            MonitorLog.Push(_logger, "account");
            _accountDirty = true;
            Poke();
        }

        var firstRead = true;
        connection.UsageChanged += OnUsageSignal;
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
                        MonitorLog.AuthRequired(_logger);
                        _observer.OnAuthRequired();
                        await WaitAsync(wake, _timing.AuthRetryInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                // The first read after connecting includes credit details (expiry shown in UIs);
                // later background polls stay light.
                var usage = await connection.GetUsageAsync(includeCreditDetails: firstRead, cancellationToken).ConfigureAwait(false);
                if (!firstRead && LimitEvaluator.IsBlocked(usage))
                {
                    // Credit details (expiry, id) are only needed once a reset is on the table.
                    usage = await connection.GetUsageAsync(includeCreditDetails: true, cancellationToken).ConfigureAwait(false);
                }

                firstRead = false;
                onHealthyRead();
                var assessment = LimitEvaluator.Assess(usage, _options, _time.GetUtcNow());
                MonitorLog.Usage(_logger, usage.FiveHour?.UsedPercent, usage.Weekly?.UsedPercent, usage.UsageAllowed, usage.AvailableResetCount);
                if (_wasBlocked && !assessment.Blocked)
                {
                    MonitorLog.Recovered(_logger);
                }

                _wasBlocked = assessment.Blocked;
                _observer.OnUsage(usage, assessment);
                foreach (var near in _nearLimit.Update(usage))
                {
                    MonitorLog.NearLimit(_logger, near.Window.ToString(), near.Threshold, near.UsedPercent);
                    _observer.OnNearLimit(near, usage);
                }

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
            connection.UsageChanged -= OnUsageSignal;
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
        MonitorLog.Episode(
            _logger,
            episode,
            string.Join(",", assessment.ExhaustedWindows),
            handling.ToString(),
            assessment.NoOfferReason?.ToString() ?? "-");
        _observer.OnLimitReached(notice);

        switch (handling)
        {
            case LimitHandling.AskUser:
                var accepted = await _observer.ConfirmResetAsync(notice, cancellationToken).ConfigureAwait(false);
                MonitorLog.Decision(_logger, accepted ? "accepted" : "declined", episode);
                if (!accepted)
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
            GuardMode.Confirm => _observer.CanConfirm ? LimitHandling.AskUser : LimitHandling.ReportOnly,
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
            MonitorLog.ResetFinished(_logger, report.Status.ToString());
            _observer.OnResetCompleted(report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not retried here: a persisted pending attempt is resumed by an explicit `resetme reset`.
            MonitorLog.ResetFailed(_logger, ex);
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
