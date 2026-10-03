using ResetMe.Core.Domain;
using ResetMe.Core.Monitoring;
using ResetMe.Core.Policies;
using ResetMe.Platform;

namespace ResetMe.Core.Tests;

public sealed class NearLimitTests : IDisposable
{
    private static readonly DateTimeOffset Resets = Usage.Now.AddHours(3);
    private readonly string _dir = Directory.CreateTempSubdirectory("resetme-near-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CodexUsage At(double fiveHour, double weekly = 10, DateTimeOffset? resets = null) => Usage.Healthy() with
    {
        FiveHour = new UsageWindow(fiveHour, 300, resets ?? Resets),
        Weekly = new UsageWindow(weekly, 10080, Usage.Now.AddDays(4)),
    };

    [Fact]
    public void Each_threshold_is_reported_once()
    {
        var tracker = new NearLimitTracker([80, 90, 95]);

        Assert.Empty(tracker.Update(At(79)));
        Assert.Equal([80], tracker.Update(At(81)).Select(n => n.Threshold));
        Assert.Empty(tracker.Update(At(85)));
        Assert.Equal([90], tracker.Update(At(90)).Select(n => n.Threshold));
        Assert.Empty(tracker.Update(At(91)));
    }

    [Fact]
    public void Jumping_several_thresholds_gives_one_notice_for_the_highest()
    {
        var tracker = new NearLimitTracker([80, 90, 95]);

        var notice = Assert.Single(tracker.Update(At(97)));

        Assert.Equal(95, notice.Threshold);
        Assert.Equal(LimitWindowKind.FiveHour, notice.Window);
    }

    [Fact]
    public void A_new_window_period_starts_over()
    {
        var tracker = new NearLimitTracker([80]);
        Assert.Single(tracker.Update(At(85)));

        Assert.Empty(tracker.Update(At(85, resets: Resets.AddSeconds(30)))); // jitter: same period
        Assert.Single(tracker.Update(At(85, resets: Resets.AddHours(5))));  // next window
    }

    [Fact]
    public void Exhausted_window_is_left_to_the_limit_flow()
    {
        var tracker = new NearLimitTracker([80, 90]);

        Assert.Empty(tracker.Update(At(100)));
        Assert.Empty(tracker.Update(At(99))); // dropping back below 100 in the same period stays quiet
    }

    [Fact]
    public void Windows_are_tracked_independently()
    {
        var tracker = new NearLimitTracker([80]);

        var notices = tracker.Update(At(85, weekly: 82));

        Assert.Equal([LimitWindowKind.FiveHour, LimitWindowKind.Weekly], notices.Select(n => n.Window));
    }

    [Fact]
    public void Invalid_thresholds_are_ignored()
    {
        var tracker = new NearLimitTracker([0, 100, 150, 90, 90]);

        Assert.Equal([90], tracker.Update(At(99)).Select(n => n.Threshold));
    }

    [Fact]
    public void Notification_text_mentions_window_level_reset_and_credits()
    {
        var text = NotificationTexts.ForNearLimit(new NearLimitNotice(LimitWindowKind.Weekly, 90, 91.4, Usage.Now.AddDays(2)), 2, Usage.Now);

        Assert.Equal("Codex weekly usage at 91%", text.Title);
        Assert.Equal("Resets in 2d 0h. 2 reset credit(s) available.", text.Body);
    }

    [Fact]
    public void Thresholds_are_read_from_config_and_validated()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "[near_limit]\nenabled = true\nthresholds = [95, 70, 70, 120]\n");

        var result = new TomlConfigStore(path).Load();

        Assert.Equal([70, 95], result.Options.NearLimit.Thresholds);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Near_limit_settings_round_trip()
    {
        var path = Path.Combine(_dir, "config.toml");
        var options = new GuardOptions();
        options.NearLimit.Enabled = false;
        options.NearLimit.Thresholds = [75, 85];
        new TomlConfigStore(path).Save(options);

        var loaded = new TomlConfigStore(path).Load().Options.NearLimit;

        Assert.False(loaded.Enabled);
        Assert.Equal([75, 85], loaded.Thresholds);
    }
}
