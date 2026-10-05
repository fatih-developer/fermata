using Fermata.Core.Domain;
using Fermata.Core.Policies;

namespace Fermata.Core.Monitoring;

/// <summary>
/// The monitor's last known view, written to disk after every read so short-lived readers (Codex
/// hooks, the MCP server, the status line) answer instantly without starting a Codex app server.
/// </summary>
public sealed record StatusSnapshot
{
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Which front end wrote it: desktop, daemon or watch.</summary>
    public required string Source { get; init; }

    public required GuardMode Mode { get; init; }

    public required int PollSeconds { get; init; }

    public double? FiveHourPercent { get; init; }

    public DateTimeOffset? FiveHourResetsAt { get; init; }

    public double? WeeklyPercent { get; init; }

    public DateTimeOffset? WeeklyResetsAt { get; init; }

    public bool? UsageAllowed { get; init; }

    public bool Blocked { get; init; }

    public IReadOnlyList<LimitWindowKind> ExhaustedWindows { get; init; } = [];

    public long ResetCredits { get; init; }

    public NoOfferReason? NoOfferReason { get; init; }

    public DateTimeOffset? NaturalUnblockAt { get; init; }

    /// <summary>Last reset result or failure, for the hook/MCP texts.</summary>
    public string? LastEvent { get; init; }

    public DateTimeOffset? LastEventAt { get; init; }

    public double PeakPercent => Math.Max(FiveHourPercent ?? 0, WeeklyPercent ?? 0);

    public static StatusSnapshot From(CodexUsage usage, LimitAssessment assessment, GuardOptions options, string source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(options);
        return new StatusSnapshot
        {
            UpdatedAt = now,
            Source = source,
            Mode = options.Mode,
            PollSeconds = Math.Max(MonitorOptions.MinimumIntervalSeconds, options.Monitor.IntervalSeconds),
            FiveHourPercent = usage.FiveHour?.UsedPercent,
            FiveHourResetsAt = usage.FiveHour?.ResetsAt,
            WeeklyPercent = usage.Weekly?.UsedPercent,
            WeeklyResetsAt = usage.Weekly?.ResetsAt,
            UsageAllowed = usage.UsageAllowed,
            Blocked = assessment.Blocked,
            ExhaustedWindows = assessment.ExhaustedWindows,
            ResetCredits = usage.AvailableResetCount,
            NoOfferReason = assessment.NoOfferReason,
            NaturalUnblockAt = assessment.NaturalUnblockAt,
        };
    }

    /// <summary>
    /// Written recently enough to trust: three poll intervals, and at least the slower blocked-state
    /// poll (60 s) three times over, so a blocked monitor does not look dead.
    /// </summary>
    public bool IsFresh(DateTimeOffset now) =>
        now - UpdatedAt <= TimeSpan.FromSeconds(Math.Max(PollSeconds, 60) * 3);
}
