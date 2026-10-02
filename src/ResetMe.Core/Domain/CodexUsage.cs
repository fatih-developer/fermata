namespace ResetMe.Core.Domain;

/// <summary>One rolling usage window (5-hour or weekly).</summary>
public sealed record UsageWindow(
    double UsedPercent,
    int? WindowDurationMinutes,
    DateTimeOffset? ResetsAt)
{
    public bool IsExhausted => UsedPercent >= 100;
}

public enum ResetCreditStatus
{
    Unknown,
    Available,
    Redeeming,
    Redeemed,
}

public sealed record ResetCredit(
    string Id,
    ResetCreditStatus Status,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ExpiresAt,
    string? Title);

/// <summary>
/// Snapshot of the account's Codex usage, independent of the wire protocol.
/// </summary>
public sealed record CodexUsage
{
    public string? AccountId { get; init; }

    /// <summary>
    /// Backend's authoritative "may the user keep working" flag. Null means unknown;
    /// recovery must never be inferred from percentages alone when this is null.
    /// </summary>
    public bool? UsageAllowed { get; init; }

    /// <summary>Raw backend reason when a limit is hit (e.g. rate_limit_reached, workspace_*).</summary>
    public string? ReachedType { get; init; }

    public UsageWindow? FiveHour { get; init; }

    public UsageWindow? Weekly { get; init; }

    public long AvailableResetCount { get; init; }

    /// <summary>False when the backend sent no reset-credit summary at all (capability missing).</summary>
    public bool ResetCreditsReported { get; init; }

    /// <summary>Credit detail rows; null when only the count is known.</summary>
    public IReadOnlyList<ResetCredit>? Credits { get; init; }

    public DateTimeOffset ReadAt { get; init; }

    public bool IsWorkspaceLimit =>
        ReachedType is not null && ReachedType.StartsWith("workspace_", StringComparison.Ordinal);
}

public sealed record AccountStatus(
    bool IsAuthenticated,
    string? AccountType,
    string? PlanType);
