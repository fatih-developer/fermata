using ResetMe.Core.Domain;
using ResetMe.Core.Policies;
using ResetMe.Core.Ports;
using ResetMe.Core.Reset;

namespace ResetMe.Core.Monitoring;

/// <summary>
/// Adds native notifications to any front end: one per limit episode and one per reset result
/// (PRD FR-07, §32). Notifications are fire-and-forget; the inner observer is never delayed.
/// </summary>
public sealed class NotifyingObserver : IMonitorObserver
{
    private readonly IMonitorObserver _inner;
    private readonly INotifier _notifier;
    private readonly TimeProvider _time;
    private readonly string _askHint;

    public NotifyingObserver(IMonitorObserver inner, INotifier notifier, TimeProvider time, string askHint)
    {
        _inner = inner;
        _notifier = notifier;
        _time = time;
        _askHint = askHint;
    }

    /// <summary>The most recent notification task (tests await it).</summary>
    public Task LastNotification { get; private set; } = Task.CompletedTask;

    public bool CanConfirm => _inner.CanConfirm;

    public void OnUsage(CodexUsage usage, LimitAssessment assessment) => _inner.OnUsage(usage, assessment);

    public void OnLimitReached(LimitNotice notice)
    {
        Notify(NotificationTexts.ForLimit(notice, _time.GetUtcNow(), _askHint));
        _inner.OnLimitReached(notice);
    }

    public void OnNearLimit(NearLimitNotice notice, CodexUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        Notify(NotificationTexts.ForNearLimit(notice, usage.AvailableResetCount, _time.GetUtcNow()));
        _inner.OnNearLimit(notice, usage);
    }

    public Task<bool> ConfirmResetAsync(LimitNotice notice, CancellationToken cancellationToken) =>
        _inner.ConfirmResetAsync(notice, cancellationToken);

    public void OnResetCompleted(ResetReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Status is not (ResetRunStatus.NotBlocked or ResetRunStatus.LockBusy))
        {
            Notify(NotificationTexts.ForReport(report));
        }

        _inner.OnResetCompleted(report);
    }

    public void OnResetFailed(Exception error)
    {
        Notify(new Notification(NotificationKind.ResetProblem, "Codex reset failed", "Run `resetme reset` to finish or retry it."));
        _inner.OnResetFailed(error);
    }

    public void OnAuthRequired() => _inner.OnAuthRequired();

    public void OnUnavailable(Exception error, TimeSpan retryIn) => _inner.OnUnavailable(error, retryIn);

    private void Notify(Notification notification)
    {
        if (_notifier.IsAvailable)
        {
            LastNotification = _notifier.ShowAsync(notification, CancellationToken.None);
        }
    }
}
