using ResetMe.Core.Domain;
using ResetMe.Core.Policies;
using ResetMe.Core.Reset;
using ResetMe.Platform;

namespace ResetMe.Core.Tests;

public sealed class PlatformTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("resetme-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Default_config_template_matches_built_in_defaults()
    {
        var store = new TomlConfigStore(Path.Combine(_dir, "config.toml"));
        Assert.True(store.EnsureDefault());
        Assert.False(store.EnsureDefault());

        var result = store.Load();

        Assert.Empty(result.Warnings);
        var defaults = new GuardOptions();
        Assert.Equal(defaults.Mode, result.Options.Mode);
        Assert.Equal(defaults.Monitor.IntervalSeconds, result.Options.Monitor.IntervalSeconds);
        Assert.Equal(defaults.Reset.MinTimeToNaturalResetMinutes, result.Options.Reset.MinTimeToNaturalResetMinutes);
        Assert.Equal(defaults.Reset.VerifyTimeoutSeconds, result.Options.Reset.VerifyTimeoutSeconds);
        Assert.Equal(defaults.Automatic.MaxResetsPerWeek, result.Options.Automatic.MaxResetsPerWeek);
    }

    [Fact]
    public void Config_values_are_read_and_validated()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, """
            mode = "automatic"

            [monitor]
            interval_seconds = 3

            [limits]
            weekly = false

            [reset]
            min_time_to_natural_reset_minutes = 30
            cooldown_seconds = -5
            """);

        var result = new TomlConfigStore(path).Load();

        Assert.Equal(GuardMode.Automatic, result.Options.Mode);
        Assert.Equal(MonitorOptions.MinimumIntervalSeconds, result.Options.Monitor.IntervalSeconds);
        Assert.False(result.Options.Limits.Weekly);
        Assert.True(result.Options.Limits.FiveHour);
        Assert.Equal(30, result.Options.Reset.MinTimeToNaturalResetMinutes);
        Assert.Equal(120, result.Options.Reset.CooldownSeconds);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void Unknown_mode_falls_back_to_confirm()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "mode = \"yolo\"\n");

        var result = new TomlConfigStore(path).Load();

        Assert.Equal(GuardMode.Confirm, result.Options.Mode);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void State_round_trips_including_the_pending_attempt()
    {
        var store = new JsonResetStateStore(Path.Combine(_dir, "state.json"));
        var state = new ResetState
        {
            Pending = new PendingResetAttempt("key-1", "credit-a", "episode-1", Usage.Now),
            LastResolvedLimitEventId = "episode-0",
            LastAttemptAt = Usage.Now,
        };
        state.AddHistory(new ResetAttemptRecord(Usage.Now, "key-0", "episode-0", ResetOutcome.Reset, ResetRunStatus.Succeeded));

        store.Save(state);
        var loaded = store.Load();

        Assert.Equal(state.Pending, loaded.Pending);
        Assert.Equal("episode-0", loaded.LastResolvedLimitEventId);
        Assert.Equal(state.History, loaded.History);
        Assert.Equal(["state.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Missing_state_file_means_empty_state()
    {
        var state = new JsonResetStateStore(Path.Combine(_dir, "state.json")).Load();

        Assert.Null(state.Pending);
        Assert.Empty(state.History);
    }

    [Fact]
    public void Corrupt_state_is_reported_not_overwritten()
    {
        var path = Path.Combine(_dir, "state.json");
        File.WriteAllText(path, "null");

        Assert.Throws<InvalidDataException>(() => new JsonResetStateStore(path).Load());
    }

    [Fact]
    public void Reset_lock_is_exclusive_until_released()
    {
        var resetLock = new FileResetLock(Path.Combine(_dir, "state.lock"));

        using (var first = resetLock.TryAcquire())
        {
            Assert.NotNull(first);
            Assert.Null(resetLock.TryAcquire());
        }

        using var again = resetLock.TryAcquire();
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Concurrent_runs_consume_at_most_one_credit()
    {
        var store = new JsonResetStateStore(Path.Combine(_dir, "state.json"));
        var resetLock = new FileResetLock(Path.Combine(_dir, "state.lock"));
        var options = Usage.FastOptions();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var slow = new BlockingClient(release.Task, entered);
        var managerA = new ResetManager(slow, store, resetLock, options, TimeProvider.System);
        var managerB = new ResetManager(new BlockingClient(Task.CompletedTask), store, resetLock, options, TimeProvider.System);

        var runA = managerA.ExecuteAsync(new ResetRequest(), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var reportB = await managerB.ExecuteAsync(new ResetRequest(), CancellationToken.None);
        release.SetResult();
        var reportA = await runA.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ResetRunStatus.LockBusy, reportB.Status);
        Assert.Equal(ResetRunStatus.Succeeded, reportA.Status);
        Assert.Equal(1, slow.Consumed);
    }

    /// <summary>Holds consume open until released, to keep the lock taken.</summary>
    private sealed class BlockingClient(Task release, TaskCompletionSource? signalEntered = null)
        : Ports.ICodexUsageClient
    {
        private bool _reset;

        public int Consumed { get; private set; }

        public Task<CodexUsage> GetUsageAsync(bool includeCreditDetails, CancellationToken cancellationToken) =>
            Task.FromResult(_reset ? Usage.Healthy() : Usage.Blocked());

        public Task<AccountStatus> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AccountStatus(true, null, null));

        public async Task<ResetOutcome> ConsumeResetAsync(string idempotencyKey, string? creditId, CancellationToken cancellationToken)
        {
            signalEntered?.TrySetResult();
            await release.ConfigureAwait(false);
            Consumed++;
            _reset = true;
            return ResetOutcome.Reset;
        }
    }
}
