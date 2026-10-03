using ResetMe.Core.Domain;
using ResetMe.Core.Policies;

namespace ResetMe.Core.Monitoring;

public sealed record NearLimitNotice(LimitWindowKind Window, int Threshold, double UsedPercent, DateTimeOffset? ResetsAt);

/// <summary>
/// Near-limit warnings (PRD §33): one notice per threshold per window period. Jumping over several
/// thresholds at once yields a single notice for the highest one. A new window period (different
/// reset time) starts from scratch.
/// </summary>
public sealed class NearLimitTracker
{
    private readonly int[] _thresholds;
    private readonly Dictionary<LimitWindowKind, (long Period, int Notified)> _state = [];

    public NearLimitTracker(IEnumerable<int> thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        _thresholds = [.. thresholds.Where(t => t is > 0 and < 100).Distinct().Order()];
    }

    public IReadOnlyList<NearLimitNotice> Update(CodexUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var notices = new List<NearLimitNotice>(2);
        Check(LimitWindowKind.FiveHour, usage.FiveHour, notices);
        Check(LimitWindowKind.Weekly, usage.Weekly, notices);
        return notices;
    }

    private void Check(LimitWindowKind kind, UsageWindow? window, List<NearLimitNotice> notices)
    {
        if (window is null || _thresholds.Length == 0)
        {
            return;
        }

        // Same hour rounding as limit episode ids: tolerant of reset-time jitter.
        var period = window.ResetsAt is { } at ? (at.ToUnixTimeSeconds() + 1800) / 3600 : 0;
        var notified = _state.TryGetValue(kind, out var s) && s.Period == period ? s.Notified : 0;

        // At 100% the limit itself is reported; near-limit warnings stop there.
        var crossed = window.IsExhausted ? 0 : _thresholds.LastOrDefault(t => window.UsedPercent >= t);
        if (crossed > notified)
        {
            notices.Add(new NearLimitNotice(kind, crossed, window.UsedPercent, window.ResetsAt));
            notified = crossed;
        }

        _state[kind] = (period, Math.Max(notified, window.IsExhausted ? _thresholds[^1] : 0));
    }
}
