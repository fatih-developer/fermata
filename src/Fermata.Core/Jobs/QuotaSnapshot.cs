using Fermata.Core.Monitoring;

namespace Fermata.Core.Jobs;

public enum QuotaWindowKind
{
    FiveHour,
    Weekly,
    Other,
}

public sealed record QuotaWindow(QuotaWindowKind Kind, double UsedPercent, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Max(0, 100 - UsedPercent);

    /// <summary>300 minutes → 5-hour, 10080 → weekly (Codex reports durations, Claude names them).</summary>
    public static QuotaWindowKind KindFromMinutes(int? minutes) => minutes switch
    {
        300 => QuotaWindowKind.FiveHour,
        10080 => QuotaWindowKind.Weekly,
        _ => QuotaWindowKind.Other,
    };
}

/// <summary>Provider-neutral view of one account's usage limits at one moment.</summary>
public sealed record QuotaSnapshot
{
    public IReadOnlyList<QuotaWindow> Windows { get; init; } = [];

    /// <summary>
    /// The provider's own "may work continue" answer (Codex <c>ordinaryUsageAllowed</c>). Null when
    /// the provider has none (Claude); then only percentages and limit events count.
    /// </summary>
    public bool? UsageAllowed { get; init; }

    /// <summary>The provider said it stopped on the limit (Claude limit notification, Codex usageLimited).</summary>
    public bool LimitReported { get; init; }

    /// <summary>Where it came from: "codex-monitor", "claude-statusline"…</summary>
    public required string Source { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }

    public QuotaWindow? Window(QuotaWindowKind kind) => Windows.FirstOrDefault(w => w.Kind == kind);

    /// <summary>From the Codex monitor's status.json.</summary>
    public static QuotaSnapshot FromCodex(StatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var windows = new List<QuotaWindow>();
        if (snapshot.FiveHourPercent is { } five)
        {
            windows.Add(new QuotaWindow(QuotaWindowKind.FiveHour, five, snapshot.FiveHourResetsAt));
        }

        if (snapshot.WeeklyPercent is { } weekly)
        {
            windows.Add(new QuotaWindow(QuotaWindowKind.Weekly, weekly, snapshot.WeeklyResetsAt));
        }

        return new QuotaSnapshot
        {
            Windows = windows,
            UsageAllowed = snapshot.UsageAllowed,
            Source = "codex-monitor",
            CapturedAt = snapshot.UpdatedAt,
        };
    }
}
