using ResetMe.Core.Domain;
using ResetMe.Core.Policies;

namespace ResetMe.Core.Tests;

public class LimitEvaluatorTests
{
    private static readonly DateTimeOffset Now = Usage.Now;

    [Fact]
    public void Backend_flag_overrides_percentages_when_allowed()
    {
        var usage = Usage.Blocked(fiveHour: 100, allowed: true);

        var result = LimitEvaluator.Assess(usage, new GuardOptions(), Now);

        Assert.False(result.Blocked);
        Assert.Equal(MonitorState.Healthy, result.State);
    }

    [Fact]
    public void Backend_flag_blocks_even_below_100_percent()
    {
        var usage = Usage.Blocked(fiveHour: 80, weekly: 50, allowed: false);

        var result = LimitEvaluator.Assess(usage, new GuardOptions(), Now);

        Assert.True(result.Blocked);
        Assert.Empty(result.ExhaustedWindows);
        Assert.Null(result.NaturalUnblockAt);
        Assert.Equal(MonitorState.ResetAvailable, result.State);
    }

    [Theory]
    [InlineData(100, 40, true)]
    [InlineData(30, 100, true)]
    [InlineData(99.9, 99, false)]
    public void Unknown_flag_falls_back_to_percentages(double fiveHour, double weekly, bool blocked)
    {
        var usage = Usage.Blocked(fiveHour, weekly, allowed: null);

        Assert.Equal(blocked, LimitEvaluator.IsBlocked(usage));
    }

    [Fact]
    public void Weekly_limit_alone_is_enough_for_an_offer()
    {
        var usage = Usage.Blocked(fiveHour: 38, weekly: 100, credits: 2);

        var result = LimitEvaluator.Assess(usage, new GuardOptions(), Now);

        Assert.Equal([LimitWindowKind.Weekly], result.ExhaustedWindows);
        Assert.True(result.ResetOffered);
    }

    [Fact]
    public void No_offer_when_limit_lifts_on_its_own_soon()
    {
        var usage = Usage.Blocked() with
        {
            FiveHour = new UsageWindow(100, 300, Now.AddMinutes(6)),
        };

        var result = LimitEvaluator.Assess(usage, new GuardOptions(), Now);

        Assert.Equal(MonitorState.LimitReached, result.State);
        Assert.Equal(NoOfferReason.NaturalResetSoon, result.NoOfferReason);
        Assert.Equal(Now.AddMinutes(6), result.NaturalUnblockAt);
    }

    [Fact]
    public void Natural_reset_threshold_can_be_ignored()
    {
        var usage = Usage.Blocked() with { FiveHour = new UsageWindow(100, 300, Now.AddMinutes(6)) };

        var result = LimitEvaluator.Assess(usage, new GuardOptions(), Now, ignoreNaturalResetThreshold: true);

        Assert.True(result.ResetOffered);
    }

    [Fact]
    public void Natural_unblock_waits_for_every_exhausted_window()
    {
        // 5h lifts in 6 minutes but the weekly window stays closed for days: still worth a credit.
        var usage = Usage.Blocked(fiveHour: 100, weekly: 100) with
        {
            FiveHour = new UsageWindow(100, 300, Now.AddMinutes(6)),
        };

        var result = LimitEvaluator.Assess(usage, new GuardOptions(), Now);

        Assert.Equal(Now.AddDays(3), result.NaturalUnblockAt);
        Assert.True(result.ResetOffered);
    }

    [Fact]
    public void Workspace_limit_is_never_offered()
    {
        var usage = Usage.Blocked() with { ReachedType = "workspace_member_usage_limit_reached" };

        var result = LimitEvaluator.Assess(usage, new GuardOptions(), Now);

        Assert.Equal(NoOfferReason.WorkspaceLimit, result.NoOfferReason);
    }

    [Fact]
    public void No_credit_is_reported()
    {
        var result = LimitEvaluator.Assess(Usage.Blocked(credits: 0), new GuardOptions(), Now);

        Assert.Equal(NoOfferReason.NoCredit, result.NoOfferReason);
        Assert.Equal(MonitorState.LimitReached, result.State);
    }

    [Fact]
    public void Disabled_window_is_not_offered()
    {
        var options = new GuardOptions();
        options.Limits.FiveHour = false;

        var result = LimitEvaluator.Assess(Usage.Blocked(fiveHour: 100, weekly: 50), options, Now);

        Assert.Equal(NoOfferReason.WindowDisabled, result.NoOfferReason);
    }

    [Fact]
    public void Earliest_expiring_available_credit_is_selected()
    {
        ResetCredit[] credits =
        [
            new("late", ResetCreditStatus.Available, Now, Now.AddDays(25), null),
            new("redeeming", ResetCreditStatus.Redeeming, Now, Now.AddDays(1), null),
            new("early", ResetCreditStatus.Available, Now, Now.AddDays(3), null),
            new("never", ResetCreditStatus.Available, Now, null, null),
        ];

        Assert.Equal("early", LimitEvaluator.SelectCredit(credits)?.Id);
    }

    [Fact]
    public void Credit_selection_defers_to_backend_without_details()
    {
        Assert.Null(LimitEvaluator.SelectCredit(null));
    }

    [Fact]
    public void Event_id_is_stable_within_an_episode()
    {
        var first = Usage.Blocked(fiveHour: 100);
        var later = first with { Weekly = first.Weekly! with { UsedPercent = 71 } };

        Assert.Equal(
            LimitEvaluator.ComputeLimitEventId(first, LimitEvaluator.ExhaustedWindows(first)),
            LimitEvaluator.ComputeLimitEventId(later, LimitEvaluator.ExhaustedWindows(later)));
    }

    [Fact]
    public void Event_id_changes_when_the_window_rolls_over()
    {
        var first = Usage.Blocked(fiveHour: 100);
        var next = first with { FiveHour = first.FiveHour! with { ResetsAt = Now.AddHours(7) } };

        Assert.NotEqual(
            LimitEvaluator.ComputeLimitEventId(first, LimitEvaluator.ExhaustedWindows(first)),
            LimitEvaluator.ComputeLimitEventId(next, LimitEvaluator.ExhaustedWindows(next)));
    }

    [Fact]
    public void Event_id_differs_per_account()
    {
        var a = Usage.Blocked();
        var b = a with { AccountId = "acct-2" };
        var windows = LimitEvaluator.ExhaustedWindows(a);

        Assert.NotEqual(LimitEvaluator.ComputeLimitEventId(a, windows), LimitEvaluator.ComputeLimitEventId(b, windows));
    }
}
