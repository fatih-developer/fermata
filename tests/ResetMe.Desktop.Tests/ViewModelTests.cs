using ResetMe.Core.Monitoring;
using ResetMe.Core.Policies;
using ResetMe.Desktop.ViewModels;
using Xunit;

namespace ResetMe.Desktop.Tests;

public class ViewModelTests
{
    private static MainViewModel NewViewModel() => new(() => Task.CompletedTask, () => Task.CompletedTask, () => { });

    [Fact]
    public void Healthy_usage_shows_monitoring_and_no_reset()
    {
        var vm = NewViewModel();
        var usage = Fixtures.Healthy(42, 30);

        vm.ApplyUsage(usage, LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now), Fixtures.Now);

        Assert.Equal(HealthKind.Ok, vm.Health);
        Assert.Equal("Monitoring", vm.StatusText);
        Assert.Equal("42%", vm.FiveHourText);
        Assert.Equal("resets in 3h 0m", vm.FiveHourResetText);
        Assert.Equal("2", vm.CreditsText);
        Assert.Equal("next expires in 9d 0h", vm.CreditExpiryText);
        Assert.False(vm.CanResetNow);
        Assert.False(vm.ResetNowCommand.CanExecute(null));
    }

    [Fact]
    public void High_usage_turns_the_light_amber()
    {
        var vm = NewViewModel();
        var usage = Fixtures.Healthy(85, 30);

        vm.ApplyUsage(usage, LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now), Fixtures.Now);

        Assert.Equal(HealthKind.Warning, vm.Health);
        Assert.Equal(85, vm.PeakPercent);
    }

    [Fact]
    public void Blocked_with_credits_enables_reset_now()
    {
        var vm = NewViewModel();
        var usage = Fixtures.Blocked();

        vm.ApplyUsage(usage, LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now), Fixtures.Now);

        Assert.Equal(HealthKind.Blocked, vm.Health);
        Assert.Equal("Limit reached", vm.StatusText);
        Assert.True(vm.ResetNowCommand.CanExecute(null));
        Assert.Contains("5h 100%", vm.TrayToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Blocked_without_credits_explains_why()
    {
        var vm = NewViewModel();
        var usage = Fixtures.Blocked(credits: 0);

        vm.ApplyUsage(usage, LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now), Fixtures.Now);

        Assert.Equal("No reset credits left.", vm.StatusDetail);
        Assert.False(vm.CanResetNow);
    }

    [Fact]
    public void Offline_disables_reset_and_updates_tooltip()
    {
        var vm = NewViewModel();
        var usage = Fixtures.Blocked();
        vm.ApplyUsage(usage, LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now), Fixtures.Now);

        vm.SetOffline("Codex unavailable", "Retrying in 5s.");

        Assert.Equal(HealthKind.Offline, vm.Health);
        Assert.False(vm.CanResetNow);
        Assert.Equal("ResetMe — Codex unavailable", vm.TrayToolTip);
    }

    [Fact]
    public void Event_list_is_capped_newest_first()
    {
        var vm = NewViewModel();
        for (var i = 0; i < MainViewModel.MaxEvents + 10; i++)
        {
            vm.AddEvent($"event {i}", Fixtures.Now);
        }

        Assert.Equal(MainViewModel.MaxEvents, vm.Events.Count);
        Assert.EndsWith($"event {MainViewModel.MaxEvents + 9}", vm.Events[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_round_trip_and_interval_is_clamped()
    {
        var vm = NewViewModel();
        var options = new GuardOptions { Mode = GuardMode.Automatic, NotificationsEnabled = false };
        options.Monitor.IntervalSeconds = 45;
        vm.LoadSettings(options, startAtLogin: true);

        Assert.Equal(2, vm.ModeIndex);
        Assert.Equal(45, vm.IntervalSeconds);
        Assert.True(vm.StartAtLogin);

        vm.ModeIndex = 0;
        vm.IntervalSeconds = 3;
        var updated = vm.ApplySettings(new GuardOptions());

        Assert.Equal(GuardMode.Manual, updated.Mode);
        Assert.Equal(MonitorOptions.MinimumIntervalSeconds, updated.Monitor.IntervalSeconds);
        Assert.False(updated.NotificationsEnabled);
    }

    [Fact]
    public void Confirmation_text_covers_prd_section_38()
    {
        var usage = Fixtures.Blocked();
        var assessment = LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now);

        var content = new ConfirmViewModel(new LimitNotice(usage, assessment, LimitHandling.AskUser), Fixtures.Now);

        Assert.Equal("5-hour Codex limit reached.", content.Headline);
        Assert.Equal("Lifts on its own in 2h 40m.", content.LiftsText);
        Assert.Equal("5-hour usage: 100%", content.FiveHourLine);
        Assert.Equal("Weekly usage: 64%", content.WeeklyLine);
        Assert.Equal("Available reset credits: 2", content.CreditsLine);
        Assert.Equal("Uses \"Full reset (Weekly + 5 hr)\", expires in 9d 0h", content.CreditDetail);
        Assert.False(content.HasWarning);
    }

    [Fact]
    public void Confirmation_warns_when_the_limit_lifts_soon()
    {
        var usage = Fixtures.Blocked() with { FiveHour = new Core.Domain.UsageWindow(100, 300, Fixtures.Now.AddMinutes(5)) };
        var assessment = LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now);

        var content = new ConfirmViewModel(new LimitNotice(usage, assessment, LimitHandling.AskUser), Fixtures.Now);

        Assert.True(content.HasWarning);
        Assert.Contains("lifts on its own soon", content.Warning, StringComparison.Ordinal);
    }
}
