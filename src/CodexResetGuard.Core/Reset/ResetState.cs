using CodexResetGuard.Core.Domain;

namespace CodexResetGuard.Core.Reset;

/// <summary>
/// Persisted reset bookkeeping (PRD §19). The pending fields are written before consume is sent
/// and cleared only once the attempt has a definitive outcome.
/// </summary>
public sealed class ResetState
{
    public const int MaxHistory = 50;

    public PendingResetAttempt? Pending { get; set; }

    /// <summary>Last limit episode that already had a resolved attempt; blocks a second one.</summary>
    public string? LastResolvedLimitEventId { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public List<ResetAttemptRecord> History { get; set; } = [];

    public void AddHistory(ResetAttemptRecord record)
    {
        History.Add(record);
        if (History.Count > MaxHistory)
        {
            History.RemoveRange(0, History.Count - MaxHistory);
        }
    }

    public int CountSuccessfulSince(DateTimeOffset since) =>
        History.Count(h => h.At >= since && h.Result == ResetRunStatus.Succeeded);
}

public sealed record PendingResetAttempt(
    string IdempotencyKey,
    string? CreditId,
    string LimitEventId,
    DateTimeOffset StartedAt);

public sealed record ResetAttemptRecord(
    DateTimeOffset At,
    string IdempotencyKey,
    string LimitEventId,
    ResetOutcome? Outcome,
    ResetRunStatus Result);
