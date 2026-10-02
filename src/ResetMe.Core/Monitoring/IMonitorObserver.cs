using ResetMe.Core.Domain;
using ResetMe.Core.Policies;
using ResetMe.Core.Reset;

namespace ResetMe.Core.Monitoring;

/// <summary>What the monitor is going to do about a newly seen limit episode.</summary>
public enum LimitHandling
{
    /// <summary>Confirm mode: the user is asked next.</summary>
    AskUser,

    /// <summary>Automatic mode: a reset is attempted without asking.</summary>
    ResetAutomatically,

    /// <summary>Manual mode: only reported; the user runs <c>resetme reset</c>.</summary>
    ReportOnly,

    /// <summary>Blocked, but policy declined (no credit, lifts soon, workspace limit…).</summary>
    NotOffered,

    /// <summary>A reset was already attempted for this episode (possibly in an earlier session).</summary>
    AlreadyHandled,
}

public sealed record LimitNotice(CodexUsage Usage, LimitAssessment Assessment, LimitHandling Handling);

/// <summary>
/// Presentation side of <see cref="RateLimitMonitor"/> (terminal now, tray/notifications later).
/// Each limit episode produces at most one <see cref="OnLimitReached"/> (PRD §32: no spam).
/// </summary>
public interface IMonitorObserver
{
    /// <summary>
    /// False when nobody can answer a prompt (headless daemon, redirected stdin): confirm mode then
    /// only reports the limit instead of asking.
    /// </summary>
    bool CanConfirm => true;

    void OnUsage(CodexUsage usage, LimitAssessment assessment);

    void OnLimitReached(LimitNotice notice);

    /// <summary>Confirm mode only. Returning false means "wait"; the episode is not offered again.</summary>
    Task<bool> ConfirmResetAsync(LimitNotice notice, CancellationToken cancellationToken);

    void OnResetCompleted(ResetReport report);

    void OnResetFailed(Exception error);

    void OnAuthRequired();

    /// <summary>Codex could not be reached; the monitor retries with backoff.</summary>
    void OnUnavailable(Exception error, TimeSpan retryIn);
}
