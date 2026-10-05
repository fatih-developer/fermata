using Fermata.Core.Jobs;
using Fermata.Core.Monitoring;
using Fermata.Core.Policies;

namespace Fermata.Core.Tests;

public sealed class QuotaPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly QuotaPolicyOptions Options = new(PrepareRemaining: 10, StopRemaining: 5, GraceSeconds: 90);

    private static QuotaSnapshot Snapshot(bool? allowed = null, bool limit = false, params QuotaWindow[] windows) =>
        new() { Windows = windows, UsageAllowed = allowed, LimitReported = limit, Source = "test", CapturedAt = Now };

    private static QuotaWindow Five(double used, int resetMinutes = 60) => new(QuotaWindowKind.FiveHour, used, Now.AddMinutes(resetMinutes));

    private static QuotaWindow Week(double used, int resetMinutes = 3000) => new(QuotaWindowKind.Weekly, used, Now.AddMinutes(resetMinutes));

    [Theory]
    [InlineData(300, QuotaWindowKind.FiveHour)]
    [InlineData(10080, QuotaWindowKind.Weekly)]
    [InlineData(60, QuotaWindowKind.Other)]
    [InlineData(null, QuotaWindowKind.Other)]
    public void Windows_are_classified_by_duration(int? minutes, QuotaWindowKind expected) =>
        Assert.Equal(expected, QuotaWindow.KindFromMinutes(minutes));

    [Fact]
    public void No_snapshot_is_unknown_and_never_stops_work()
    {
        var result = QuotaPolicy.Assess(null, Options, Now);
        Assert.Equal(QuotaLevel.Unknown, result.Level);
        Assert.False(result.AtLeast(QuotaLevel.Prepare));
    }

    [Theory]
    [InlineData(89, QuotaLevel.Normal)]
    [InlineData(90, QuotaLevel.Prepare)]
    [InlineData(94, QuotaLevel.Prepare)]
    [InlineData(95, QuotaLevel.StopNewWork)]
    [InlineData(99, QuotaLevel.StopNewWork)]
    [InlineData(100, QuotaLevel.Blocked)]
    public void Threshold_boundaries_use_percent_remaining(double used, QuotaLevel expected) =>
        Assert.Equal(expected, QuotaPolicy.Assess(Snapshot(null, false, Five(used), Week(10)), Options, Now).Level);

    [Fact]
    public void Resume_time_is_the_latest_blocking_reset_plus_grace()
    {
        var result = QuotaPolicy.Assess(Snapshot(false, false, Five(100, 60), Week(100, 600)), Options, Now);

        Assert.Equal(QuotaLevel.Blocked, result.Level);
        Assert.Equal(Now.AddMinutes(600).AddSeconds(90), result.ResumeAt);
        Assert.Equal([QuotaWindowKind.FiveHour, QuotaWindowKind.Weekly], result.CriticalWindows);
    }

    [Fact]
    public void Only_blocking_windows_set_the_resume_time()
    {
        var result = QuotaPolicy.Assess(Snapshot(null, false, Five(100, 60), Week(40, 600)), Options, Now);
        Assert.Equal(Now.AddMinutes(60).AddSeconds(90), result.ResumeAt);
    }

    [Fact]
    public void Codex_usage_allowed_is_authoritative_both_ways()
    {
        // Allowed at 100 %: not blocked (but stop new work).
        Assert.Equal(QuotaLevel.StopNewWork, QuotaPolicy.Assess(Snapshot(true, false, Five(100)), Options, Now).Level);

        // Refused below 100 % (e.g. a workspace limit): blocked.
        Assert.Equal(QuotaLevel.Blocked, QuotaPolicy.Assess(Snapshot(false, false, Five(50)), Options, Now).Level);
    }

    [Fact]
    public void A_window_whose_reset_passed_counts_as_empty()
    {
        var result = QuotaPolicy.Assess(Snapshot(null, false, Five(100, -5), Week(20)), Options, Now);
        Assert.Equal(QuotaLevel.Normal, result.Level);
    }

    [Fact]
    public void A_reported_limit_blocks_until_its_windows_reset()
    {
        Assert.Equal(QuotaLevel.Blocked, QuotaPolicy.Assess(Snapshot(null, true, Five(97, 30)), Options, Now).Level);
        Assert.Equal(QuotaLevel.Normal, QuotaPolicy.Assess(Snapshot(null, true, Five(97, -1)), Options, Now).Level);
    }

    [Fact]
    public void Codex_status_snapshot_maps_to_a_quota_snapshot()
    {
        var snapshot = new StatusSnapshot
        {
            UpdatedAt = Now,
            Source = "daemon",
            Mode = GuardMode.Confirm,
            PollSeconds = 30,
            FiveHourPercent = 42,
            FiveHourResetsAt = Now.AddHours(1),
            WeeklyPercent = 7,
            UsageAllowed = true,
        };

        var quota = QuotaSnapshot.FromCodex(snapshot);

        Assert.Equal(42, quota.Window(QuotaWindowKind.FiveHour)!.UsedPercent);
        Assert.Equal(7, quota.Window(QuotaWindowKind.Weekly)!.UsedPercent);
        Assert.True(quota.UsageAllowed);
    }
}
