using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResetMe.Core.Domain;
using ResetMe.Core.Logging;
using ResetMe.Core.Policies;
using ResetMe.Core.Ports;

namespace ResetMe.Core.Reset;

public enum ResetRunStatus
{
    /// <summary>Credit redeemed and usage verified as unblocked.</summary>
    Succeeded,

    /// <summary>Outcome could not be verified; no further automatic attempt is made.</summary>
    Unconfirmed,

    NotBlocked,
    NothingToReset,
    NoCredit,

    /// <summary>Blocked, but policy declined (see <see cref="ResetReport.NoOfferReason"/>).</summary>
    NotOffered,

    /// <summary>This limit episode already had a resolved attempt.</summary>
    AlreadyHandled,

    CooldownActive,
    AutomaticCapReached,

    /// <summary>Automatic mode never resumes or overrides an unresolved attempt.</summary>
    PendingAttemptNeedsUser,

    /// <summary>Another ResetMe process holds the reset lock.</summary>
    LockBusy,
}

/// <param name="Force">
/// Manual override for the natural-reset threshold, cooldown and the already-handled guard.
/// Never overrides "not blocked", "no credit" or a workspace limit.
/// </param>
public sealed record ResetRequest(bool Force = false, bool Automatic = false);

public sealed record ResetReport(
    ResetRunStatus Status,
    ResetOutcome? Outcome = null,
    LimitAssessment? Assessment = null,
    CodexUsage? UsageBefore = null,
    CodexUsage? UsageAfter = null,
    string? IdempotencyKey = null,
    bool ResumedPendingAttempt = false,
    NoOfferReason? NoOfferReason = null);

/// <summary>
/// Runs one reset attempt end to end with the double-reset protections of PRD §19:
/// cross-process lock, persisted idempotency key, per-episode guard and verification.
/// </summary>
public sealed class ResetManager
{
    private readonly ICodexUsageClient _client;
    private readonly IResetStateStore _store;
    private readonly IResetLock _lock;
    private readonly GuardOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public ResetManager(
        ICodexUsageClient client,
        IResetStateStore store,
        IResetLock resetLock,
        GuardOptions options,
        TimeProvider time,
        ILogger<ResetManager>? logger = null)
    {
        _client = client;
        _store = store;
        _lock = resetLock;
        _options = options;
        _time = time;
        _logger = logger ?? NullLogger<ResetManager>.Instance;
    }

    public event Action<MonitorState>? StateChanged;

    public async Task<ResetReport> ExecuteAsync(ResetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ResetLog.RunStarted(_logger, request.Force, request.Automatic);

        var report = await ExecuteCoreAsync(request, cancellationToken).ConfigureAwait(false);
        if (report.Outcome is null && report.Status is not (ResetRunStatus.Succeeded or ResetRunStatus.Unconfirmed))
        {
            ResetLog.NotAttempted(_logger, report.Status.ToString(), report.NoOfferReason?.ToString() ?? "-");
        }

        return report;
    }

    private async Task<ResetReport> ExecuteCoreAsync(ResetRequest request, CancellationToken cancellationToken)
    {

        using var handle = _lock.TryAcquire();
        if (handle is null)
        {
            return new ResetReport(ResetRunStatus.LockBusy);
        }

        // Re-read under the lock: another process may have finished an attempt meanwhile.
        var state = _store.Load();
        var before = await _client.GetUsageAsync(includeCreditDetails: true, cancellationToken).ConfigureAwait(false);

        if (state.Pending is { } pending)
        {
            if (request.Automatic)
            {
                return new ResetReport(ResetRunStatus.PendingAttemptNeedsUser, UsageBefore: before);
            }

            ResetLog.Resuming(_logger, pending.IdempotencyKey, pending.StartedAt);
            return await RunAttemptAsync(state, pending, before, null, resumed: true, cancellationToken)
                .ConfigureAwait(false);
        }

        var now = _time.GetUtcNow();
        var assessment = LimitEvaluator.Assess(before, _options, now, ignoreNaturalResetThreshold: request.Force);

        if (!assessment.Blocked)
        {
            return new ResetReport(ResetRunStatus.NotBlocked, Assessment: assessment, UsageBefore: before);
        }

        if (assessment.NoOfferReason is { } reason)
        {
            var status = reason == NoOfferReason.NoCredit ? ResetRunStatus.NoCredit : ResetRunStatus.NotOffered;
            return new ResetReport(status, Assessment: assessment, UsageBefore: before, NoOfferReason: reason);
        }

        var guard = CheckGuards(request, state, before, assessment, now);
        if (guard is not null)
        {
            return new ResetReport(guard.Value, Assessment: assessment, UsageBefore: before);
        }

        var attempt = new PendingResetAttempt(
            Guid.NewGuid().ToString(),
            assessment.SelectedCredit?.Id,
            assessment.LimitEventId!,
            now);

        // Persist before sending: a crash after this point resumes with the same key.
        state.Pending = attempt;
        state.LastAttemptAt = now;
        _store.Save(state);
        ResetLog.AttemptStarted(_logger, attempt.LimitEventId, attempt.IdempotencyKey, attempt.CreditId ?? "backend-selected");

        return await RunAttemptAsync(state, attempt, before, assessment, resumed: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private ResetRunStatus? CheckGuards(
        ResetRequest request,
        ResetState state,
        CodexUsage usage,
        LimitAssessment assessment,
        DateTimeOffset now)
    {
        if (request.Automatic)
        {
            // Automatic mode only acts on an explicit backend "blocked" signal (PRD §39).
            if (usage.UsageAllowed != false)
            {
                return ResetRunStatus.NotOffered;
            }

            if (state.CountSuccessfulSince(now.AddDays(-1)) >= _options.Automatic.MaxResetsPerDay
                || state.CountSuccessfulSince(now.AddDays(-7)) >= _options.Automatic.MaxResetsPerWeek)
            {
                return ResetRunStatus.AutomaticCapReached;
            }
        }

        var force = request.Force && !request.Automatic;
        if (!force && state.LastResolvedLimitEventId == assessment.LimitEventId)
        {
            return ResetRunStatus.AlreadyHandled;
        }

        if (!force
            && state.LastAttemptAt is { } last
            && now - last < TimeSpan.FromSeconds(_options.Reset.CooldownSeconds))
        {
            return ResetRunStatus.CooldownActive;
        }

        return null;
    }

    private async Task<ResetReport> RunAttemptAsync(
        ResetState state,
        PendingResetAttempt attempt,
        CodexUsage before,
        LimitAssessment? assessment,
        bool resumed,
        CancellationToken cancellationToken)
    {
        StateChanged?.Invoke(MonitorState.Resetting);

        var outcome = await ConsumeWithRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
        if (outcome is { } known)
        {
            ResetLog.ConsumeOutcome(_logger, known.ToString(), attempt.IdempotencyKey);
        }

        switch (outcome)
        {
            case ResetOutcome.NothingToReset:
            case ResetOutcome.NoCredit:
                var definitive = outcome == ResetOutcome.NoCredit
                    ? ResetRunStatus.NoCredit
                    : ResetRunStatus.NothingToReset;
                Resolve(state, attempt, outcome, definitive, markEpisodeHandled: false);
                StateChanged?.Invoke(definitive == ResetRunStatus.NoCredit ? MonitorState.ResetFailed : MonitorState.Healthy);
                return new ResetReport(definitive, outcome, assessment, before, null, attempt.IdempotencyKey, resumed);
        }

        // Reset, AlreadyRedeemed, or unknown (transient failures exhausted): verify by reading usage.
        StateChanged?.Invoke(MonitorState.Verifying);
        var after = await VerifyAsync(cancellationToken).ConfigureAwait(false);
        var verified = after is not null && LimitEvaluator.IsUsable(after);

        if (verified)
        {
            ResetLog.Verified(_logger, after!.FiveHour?.UsedPercent, after.Weekly?.UsedPercent, after.AvailableResetCount);
            Resolve(state, attempt, outcome, ResetRunStatus.Succeeded, markEpisodeHandled: true);
            StateChanged?.Invoke(MonitorState.ResetSucceeded);
            return new ResetReport(ResetRunStatus.Succeeded, outcome, assessment, before, after, attempt.IdempotencyKey, resumed);
        }

        if (outcome is null)
        {
            // Never reached the backend with certainty: keep the pending key so the next manual
            // run retries the *same* attempt instead of starting a new one.
            state.AddHistory(new ResetAttemptRecord(_time.GetUtcNow(), attempt.IdempotencyKey, attempt.LimitEventId, null, ResetRunStatus.Unconfirmed));
            _store.Save(state);
        }
        else
        {
            // The backend answered: this key is spent. Close the episode; a new episode may try again.
            Resolve(state, attempt, outcome, ResetRunStatus.Unconfirmed, markEpisodeHandled: true);
        }

        ResetLog.Unconfirmed(_logger, attempt.IdempotencyKey, outcome?.ToString() ?? "unknown", pendingKept: outcome is null);
        StateChanged?.Invoke(MonitorState.ResetUnconfirmed);
        return new ResetReport(ResetRunStatus.Unconfirmed, outcome, assessment, before, after, attempt.IdempotencyKey, resumed);
    }

    /// <summary>Returns null when every try failed transiently and the outcome is unknown.</summary>
    private async Task<ResetOutcome?> ConsumeWithRetryAsync(PendingResetAttempt attempt, CancellationToken cancellationToken)
    {
        var tries = 1 + Math.Max(0, _options.Reset.ConsumeRetryMax);
        for (var i = 0; i < tries; i++)
        {
            try
            {
                return await _client.ConsumeResetAsync(attempt.IdempotencyKey, attempt.CreditId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CodexTransientException ex) when (i < tries - 1)
            {
                ResetLog.ConsumeRetry(_logger, ex, i + 1, tries);
                await Task.Delay(TimeSpan.FromSeconds(_options.Reset.ConsumeRetryDelaySeconds), _time, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CodexTransientException ex)
            {
                ResetLog.ConsumeUnknown(_logger, ex, tries);
                return null;
            }
        }

        return null;
    }

    private async Task<CodexUsage?> VerifyAsync(CancellationToken cancellationToken)
    {
        var reset = _options.Reset;
        var deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(reset.VerifyTimeoutSeconds);
        await Task.Delay(TimeSpan.FromSeconds(reset.VerifyAfterSeconds), _time, cancellationToken).ConfigureAwait(false);

        CodexUsage? last = null;
        while (true)
        {
            try
            {
                last = await _client.GetUsageAsync(includeCreditDetails: false, cancellationToken).ConfigureAwait(false);
                if (LimitEvaluator.IsUsable(last))
                {
                    return last;
                }
            }
            catch (CodexTransientException ex)
            {
                // Keep polling until the deadline.
                ResetLog.VerifyReadFailed(_logger, ex);
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(reset.VerifyIntervalSeconds), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Resolve(
        ResetState state,
        PendingResetAttempt attempt,
        ResetOutcome? outcome,
        ResetRunStatus result,
        bool markEpisodeHandled)
    {
        state.Pending = null;
        if (markEpisodeHandled)
        {
            state.LastResolvedLimitEventId = attempt.LimitEventId;
        }

        state.AddHistory(new ResetAttemptRecord(_time.GetUtcNow(), attempt.IdempotencyKey, attempt.LimitEventId, outcome, result));
        _store.Save(state);
    }
}
