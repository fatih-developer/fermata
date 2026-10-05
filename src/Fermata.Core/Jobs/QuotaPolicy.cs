namespace Fermata.Core.Jobs;

public enum QuotaLevel
{
    /// <summary>No snapshot: nothing is known, so nothing is stopped because of quota.</summary>
    Unknown,
    Normal,

    /// <summary>Little left: ask the agent for a handoff note while it can still write one.</summary>
    Prepare,

    /// <summary>Almost nothing left: let the current turn finish, start nothing new.</summary>
    StopNewWork,

    /// <summary>The provider refuses work until a window resets.</summary>
    Blocked,
}

/// <param name="ResumeAt">When work may continue (latest reset of the windows that matter, plus grace); null if unknown.</param>
/// <param name="MinRemainingPercent">Smallest remaining share over all windows; null without windows.</param>
public sealed record QuotaAssessment(
    QuotaLevel Level,
    DateTimeOffset? ResumeAt,
    IReadOnlyList<QuotaWindowKind> CriticalWindows,
    double? MinRemainingPercent)
{
    public static readonly QuotaAssessment Unknown = new(QuotaLevel.Unknown, null, [], null);

    public bool Blocked => Level == QuotaLevel.Blocked;

    public bool AtLeast(QuotaLevel level) => Level >= level && Level != QuotaLevel.Unknown;
}

/// <summary>Thresholds in percent remaining. There is no 2 % level: whole-number percentages cannot resolve it reliably.</summary>
public sealed record QuotaPolicyOptions(int PrepareRemaining = 10, int StopRemaining = 5, int GraceSeconds = 90)
{
    public TimeSpan Grace => TimeSpan.FromSeconds(GraceSeconds);
}

/// <summary>Pure: snapshot + time → level and resume time.</summary>
public static class QuotaPolicy
{
    public static QuotaAssessment Assess(QuotaSnapshot? snapshot, QuotaPolicyOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (snapshot is null)
        {
            return QuotaAssessment.Unknown;
        }

        // A window whose reset time has passed has started over, whatever the snapshot said.
        var windows = snapshot.Windows
            .Select(w => w.ResetsAt is { } at && at <= now ? w with { UsedPercent = 0 } : w)
            .ToList();
        var minRemaining = windows.Count == 0 ? (double?)null : windows.Min(w => w.RemainingPercent);

        var exhausted = windows.Where(w => w.RemainingPercent <= 0).ToList();
        var low = windows.Where(w => w.RemainingPercent <= options.StopRemaining).ToList();
        var preparing = windows.Where(w => w.RemainingPercent <= options.PrepareRemaining).ToList();

        // A reported limit with every window already reset is history, not a block.
        var limitStillOn = snapshot.LimitReported
            && (windows.Count == 0 || snapshot.Windows.Any(w => w.ResetsAt is null || w.ResetsAt > now));

        var blocked = snapshot.UsageAllowed switch
        {
            false => true,
            true => false,
            null => exhausted.Count > 0 || limitStillOn,
        };

        if (blocked)
        {
            var reasons = exhausted.Count > 0 ? exhausted : low.Count > 0 ? low : windows.Where(w => w.ResetsAt > now).ToList();
            return new QuotaAssessment(QuotaLevel.Blocked, ResumeAt(reasons, options), Kinds(reasons), minRemaining);
        }

        if (low.Count > 0)
        {
            return new QuotaAssessment(QuotaLevel.StopNewWork, ResumeAt(low, options), Kinds(low), minRemaining);
        }

        if (preparing.Count > 0)
        {
            return new QuotaAssessment(QuotaLevel.Prepare, ResumeAt(preparing, options), Kinds(preparing), minRemaining);
        }

        return new QuotaAssessment(QuotaLevel.Normal, null, [], minRemaining);
    }

    /// <summary>Latest reset among the windows that matter, plus grace: every one of them must have reset.</summary>
    private static DateTimeOffset? ResumeAt(IReadOnlyList<QuotaWindow> windows, QuotaPolicyOptions options)
    {
        var resets = windows.Where(w => w.ResetsAt is not null).Select(w => w.ResetsAt!.Value).ToList();
        return resets.Count == 0 ? null : resets.Max() + options.Grace;
    }

    private static IReadOnlyList<QuotaWindowKind> Kinds(IEnumerable<QuotaWindow> windows) => windows.Select(w => w.Kind).Distinct().ToList();
}
