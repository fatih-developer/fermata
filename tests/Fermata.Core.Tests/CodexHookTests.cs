using Fermata.Core.Domain;
using Fermata.Core.Monitoring;
using Fermata.Core.Policies;
using Fermata.Core.Reset;
using Fermata.Platform;
using Xunit;

namespace Fermata.Core.Tests;

public class CodexHookTests
{
    private static StatusSnapshot Snapshot(CodexUsage usage, GuardMode mode = GuardMode.Confirm, DateTimeOffset? at = null)
    {
        var options = new GuardOptions { Mode = mode };
        return StatusSnapshot.From(usage, LimitEvaluator.Assess(usage, options, Usage.Now), options, "desktop", at ?? Usage.Now);
    }

    [Fact]
    public void Session_start_without_a_monitor_says_protection_is_off()
    {
        Assert.Equal(CodexHookAdvisor.NotMonitoring, CodexHookAdvisor.ForSessionStart(null, Usage.Now));

        var stale = Snapshot(Usage.Healthy(), at: Usage.Now.AddMinutes(-10));
        Assert.Equal(CodexHookAdvisor.NotMonitoring, CodexHookAdvisor.ForSessionStart(stale, Usage.Now));
    }

    [Fact]
    public void Prompts_stay_silent_when_usage_is_normal_or_unknown()
    {
        Assert.Null(CodexHookAdvisor.ForPrompt(null, Usage.Now));
        Assert.Null(CodexHookAdvisor.ForPrompt(Snapshot(Usage.Healthy()), Usage.Now));
        Assert.Null(CodexHookAdvisor.ForSessionStart(Snapshot(Usage.Healthy()), Usage.Now));
    }

    [Fact]
    public void High_usage_is_summarised_with_credits_and_mode()
    {
        var usage = Usage.Blocked(fiveHour: 96, weekly: 41, credits: 2, allowed: true) with { ReachedType = null };

        var text = CodexHookAdvisor.ForPrompt(Snapshot(usage, GuardMode.Automatic), Usage.Now);

        Assert.Equal("Codex usage: 5-hour 96% (resets in 2h 0m), weekly 41% (resets in 3d 0h). 2 reset credits; auto-reset on.", text);
    }

    [Theory]
    [InlineData(GuardMode.Confirm, "use one from the Fermata tray icon")]
    [InlineData(GuardMode.Automatic, "Auto-reset is on")]
    [InlineData(GuardMode.Manual, "run `fermata reset`")]
    public void A_limit_explains_what_happens_next(GuardMode mode, string expected)
    {
        var text = CodexHookAdvisor.ForPrompt(Snapshot(Usage.Blocked(credits: 2), mode), Usage.Now);

        Assert.StartsWith("Codex 5-hour limit reached. Lifts on its own in 2h 0m.", text, StringComparison.Ordinal);
        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_limit_without_credits_says_so()
    {
        var text = CodexHookAdvisor.ForSessionStart(Snapshot(Usage.Blocked(credits: 0)), Usage.Now);

        Assert.EndsWith("No reset credits left.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_observer_writes_every_read_and_keeps_the_last_event()
    {
        var root = Directory.CreateTempSubdirectory("fermata-status-").FullName;
        try
        {
            var store = new JsonStatusSnapshotStore(Path.Combine(root, "status.json"));
            var observer = new SnapshotObserver(new NullObserver(), store, new GuardOptions(), "daemon", new FixedTimeProvider(Usage.Now));
            var blocked = Usage.Blocked(credits: 2);

            observer.OnUsage(blocked, LimitEvaluator.Assess(blocked, new GuardOptions(), Usage.Now));
            var first = store.Load()!;
            Assert.True(first.Blocked);
            Assert.Equal(2, first.ResetCredits);
            Assert.Equal([LimitWindowKind.FiveHour], first.ExhaustedWindows);
            Assert.Equal("daemon", first.Source);

            observer.OnResetCompleted(new ResetReport(ResetRunStatus.Succeeded));
            Assert.StartsWith("Codex reset applied", store.Load()!.LastEvent, StringComparison.Ordinal);

            var healthy = Usage.Healthy(credits: 1);
            observer.OnUsage(healthy, LimitEvaluator.Assess(healthy, new GuardOptions(), Usage.Now));
            var after = store.Load()!;
            Assert.False(after.Blocked);
            Assert.NotNull(after.LastEvent);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_corrupt_status_file_reads_as_missing()
    {
        var root = Directory.CreateTempSubdirectory("fermata-status-").FullName;
        try
        {
            var path = Path.Combine(root, "status.json");
            File.WriteAllText(path, "{ not json");
            Assert.Null(new JsonStatusSnapshotStore(path).Load());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NullObserver : IMonitorObserver
    {
        public void OnUsage(CodexUsage usage, LimitAssessment assessment)
        {
        }

        public void OnLimitReached(LimitNotice notice)
        {
        }

        public Task<bool> ConfirmResetAsync(LimitNotice notice, CancellationToken cancellationToken) => Task.FromResult(false);

        public void OnResetCompleted(ResetReport report)
        {
        }

        public void OnResetFailed(Exception error)
        {
        }

        public void OnAuthRequired()
        {
        }

        public void OnUnavailable(Exception error, TimeSpan retryIn)
        {
        }
    }
}

public class CodexUsageDescriptionTests
{
    [Fact]
    public void Describe_includes_the_last_reset_event()
    {
        var options = new GuardOptions();
        var usage = Usage.Healthy(credits: 1);
        var snapshot = StatusSnapshot.From(usage, LimitEvaluator.Assess(usage, options, Usage.Now), options, "desktop", Usage.Now)
            with { LastEvent = "Codex reset applied", LastEventAt = Usage.Now.AddMinutes(-5) };

        var text = CodexHookAdvisor.Describe(snapshot, Usage.Now);

        Assert.StartsWith("Codex usage: 5-hour 0%", text, StringComparison.Ordinal);
        Assert.EndsWith("Last Fermata event (5m ago): Codex reset applied", text, StringComparison.Ordinal);
        Assert.Equal(CodexHookAdvisor.NotMonitoring, CodexHookAdvisor.Describe(null, Usage.Now));
    }
}
