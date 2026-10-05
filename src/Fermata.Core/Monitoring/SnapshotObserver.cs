using Fermata.Core.Domain;
using Fermata.Core.Policies;
using Fermata.Core.Ports;
using Fermata.Core.Reset;

namespace Fermata.Core.Monitoring;

/// <summary>Writes a <see cref="StatusSnapshot"/> after every read and reset. Never delays or fails the monitor.</summary>
public sealed class SnapshotObserver : IMonitorObserver
{
    private readonly IMonitorObserver _inner;
    private readonly IStatusSnapshotStore _store;
    private readonly GuardOptions _options;
    private readonly string _source;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private StatusSnapshot? _last;

    public SnapshotObserver(IMonitorObserver inner, IStatusSnapshotStore store, GuardOptions options, string source, TimeProvider time)
    {
        _inner = inner;
        _store = store;
        _options = options;
        _source = source;
        _time = time;
    }

    public bool CanConfirm => _inner.CanConfirm;

    public void OnUsage(CodexUsage usage, LimitAssessment assessment)
    {
        lock (_gate)
        {
            var snapshot = StatusSnapshot.From(usage, assessment, _options, _source, _time.GetUtcNow());
            Write(_last is null ? snapshot : snapshot with { LastEvent = _last.LastEvent, LastEventAt = _last.LastEventAt });
        }

        _inner.OnUsage(usage, assessment);
    }

    public void OnLimitReached(LimitNotice notice) => _inner.OnLimitReached(notice);

    public void OnPendingAttempt(PendingResetAttempt pending) => _inner.OnPendingAttempt(pending);

    public void OnNearLimit(NearLimitNotice notice, CodexUsage usage) => _inner.OnNearLimit(notice, usage);

    public Task<bool> ConfirmResetAsync(LimitNotice notice, CancellationToken cancellationToken) =>
        _inner.ConfirmResetAsync(notice, cancellationToken);

    public void OnResetCompleted(ResetReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = NotificationTexts.ForReport(report);
        RecordEvent($"{text.Title}: {text.Body}");
        _inner.OnResetCompleted(report);
    }

    public void OnResetFailed(Exception error)
    {
        RecordEvent("Reset failed. Run `fermata reset` to finish or retry it.");
        _inner.OnResetFailed(error);
    }

    public void OnAuthRequired() => _inner.OnAuthRequired();

    public void OnUnavailable(Exception error, TimeSpan retryIn) => _inner.OnUnavailable(error, retryIn);

    private void RecordEvent(string text)
    {
        lock (_gate)
        {
            if (_last is not null)
            {
                Write(_last with { LastEvent = text, LastEventAt = _time.GetUtcNow() });
            }
        }
    }

    private void Write(StatusSnapshot snapshot)
    {
        _last = snapshot;
        try
        {
            _store.Save(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Readers fall back to "not monitoring"; the monitor itself must keep going.
        }
    }
}
