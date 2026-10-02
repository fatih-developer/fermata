using CodexResetGuard.Core.Domain;
using CodexResetGuard.Core.Policies;
using CodexResetGuard.Core.Reset;

namespace CodexResetGuard.Core.Tests;

public class ResetManagerTests
{
    private readonly FakeCodexClient _client = new();
    private readonly InMemoryStateStore _store = new();
    private readonly FakeLock _lock = new();
    private readonly SteppingTimeProvider _time = new(Usage.Now);
    private readonly GuardOptions _options = Usage.FastOptions();

    private ResetManager Manager() => new(_client, _store, _lock, _options, _time);

    private Task<ResetReport> Run(bool force = false, bool automatic = false) =>
        Manager().ExecuteAsync(new ResetRequest(force, automatic), CancellationToken.None);

    [Fact]
    public async Task Successful_reset_consumes_once_and_resolves_the_episode()
    {
        _client.ThenUsage(Usage.Blocked()).ThenConsume(ResetOutcome.Reset).ThenUsage(Usage.Healthy());

        var report = await Run();

        Assert.Equal(ResetRunStatus.Succeeded, report.Status);
        var call = Assert.Single(_client.ConsumeCalls);
        Assert.Equal("credit-a", call.CreditId);

        var state = _store.Load();
        Assert.Null(state.Pending);
        Assert.Equal(report.Assessment!.LimitEventId, state.LastResolvedLimitEventId);
        Assert.Equal(ResetRunStatus.Succeeded, Assert.Single(state.History).Result);
    }

    [Fact]
    public async Task Idempotency_key_is_persisted_before_consume_is_sent()
    {
        string? persistedAtSendTime = null;
        _client.OnConsume = _ => persistedAtSendTime = _store.Load().Pending?.IdempotencyKey;
        _client.ThenUsage(Usage.Blocked()).ThenConsume(ResetOutcome.Reset).ThenUsage(Usage.Healthy());

        await Run();

        Assert.NotNull(persistedAtSendTime);
        Assert.Equal(_client.ConsumeCalls[0].Key, persistedAtSendTime);
    }

    [Fact]
    public async Task Never_consumes_when_not_blocked()
    {
        _client.ThenUsage(Usage.Healthy(credits: 2));

        var report = await Run(force: true);

        Assert.Equal(ResetRunStatus.NotBlocked, report.Status);
        Assert.Empty(_client.ConsumeCalls);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task Never_consumes_without_the_lock()
    {
        _lock.Busy = true;
        _client.ThenUsage(Usage.Blocked());

        var report = await Run();

        Assert.Equal(ResetRunStatus.LockBusy, report.Status);
        Assert.Empty(_client.ConsumeCalls);
    }

    [Fact]
    public async Task Timeouts_are_retried_with_the_same_key()
    {
        _client.ThenUsage(Usage.Blocked())
            .ThenConsumeTimesOut()
            .ThenConsumeTimesOut()
            .ThenConsume(ResetOutcome.AlreadyRedeemed)
            .ThenUsage(Usage.Healthy());

        var report = await Run();

        Assert.Equal(ResetRunStatus.Succeeded, report.Status);
        Assert.Equal(3, _client.ConsumeCalls.Count);
        Assert.Single(_client.ConsumeCalls.Select(c => c.Key).Distinct());
    }

    [Fact]
    public async Task Unknown_outcome_keeps_the_key_and_the_next_run_reuses_it()
    {
        _options.Reset.ConsumeRetryMax = 1;
        _client.FallbackUsage = Usage.Blocked();
        _client.ThenUsage(Usage.Blocked()).ThenConsumeTimesOut().ThenConsumeTimesOut();

        var first = await Run();

        Assert.Equal(ResetRunStatus.Unconfirmed, first.Status);
        Assert.Null(first.Outcome);
        var pending = _store.Load().Pending;
        Assert.NotNull(pending);

        // Second manual run: resumes the same logical attempt instead of starting a new one.
        _client.ThenConsume(ResetOutcome.AlreadyRedeemed).ThenUsage(Usage.Blocked()).ThenUsage(Usage.Healthy());
        var second = await Run();

        Assert.True(second.ResumedPendingAttempt);
        Assert.Equal(ResetRunStatus.Succeeded, second.Status);
        Assert.All(_client.ConsumeCalls, c => Assert.Equal(pending.IdempotencyKey, c.Key));
        Assert.Null(_store.Load().Pending);
    }

    [Fact]
    public async Task Crash_after_persisting_resumes_with_the_same_key()
    {
        var crashed = new ResetState
        {
            Pending = new PendingResetAttempt("key-from-before-crash", "credit-a", "episode-1", Usage.Now.AddMinutes(-1)),
        };
        _store.Save(crashed);
        _client.ThenUsage(Usage.Blocked()).ThenConsume(ResetOutcome.AlreadyRedeemed).ThenUsage(Usage.Healthy());

        var report = await Run();

        Assert.True(report.ResumedPendingAttempt);
        Assert.Equal(("key-from-before-crash", (string?)"credit-a"), Assert.Single(_client.ConsumeCalls));
        Assert.Equal(ResetRunStatus.Succeeded, report.Status);
    }

    [Fact]
    public async Task Same_episode_is_never_reset_twice()
    {
        _options.Reset.CooldownSeconds = 0;
        _client.FallbackUsage = Usage.Blocked();
        _client.ThenUsage(Usage.Blocked()).ThenConsume(ResetOutcome.Reset);

        var first = await Run();
        Assert.Equal(ResetRunStatus.Unconfirmed, first.Status);

        _client.ThenUsage(Usage.Blocked());
        var second = await Run();

        Assert.Equal(ResetRunStatus.AlreadyHandled, second.Status);
        Assert.Single(_client.ConsumeCalls);
    }

    [Fact]
    public async Task Force_allows_a_new_attempt_for_a_handled_episode()
    {
        _options.Reset.CooldownSeconds = 0;
        _client.FallbackUsage = Usage.Blocked();
        _client.ThenUsage(Usage.Blocked()).ThenConsume(ResetOutcome.Reset);
        await Run();

        _client.ThenUsage(Usage.Blocked()).ThenConsume(ResetOutcome.Reset).ThenUsage(Usage.Healthy());
        var forced = await Run(force: true);

        Assert.Equal(ResetRunStatus.Succeeded, forced.Status);
        Assert.Equal(2, _client.ConsumeCalls.Select(c => c.Key).Distinct().Count());
    }

    [Fact]
    public async Task Cooldown_blocks_a_quick_second_attempt_for_a_new_episode()
    {
        _client.ThenUsage(Usage.Blocked()).ThenConsume(ResetOutcome.Reset).ThenUsage(Usage.Healthy());
        await Run();

        var newEpisode = Usage.Blocked() with { FiveHour = new UsageWindow(100, 300, Usage.Now.AddHours(5)) };
        _client.ThenUsage(newEpisode);
        var report = await Run();

        Assert.Equal(ResetRunStatus.CooldownActive, report.Status);
        Assert.Single(_client.ConsumeCalls);
    }

    [Fact]
    public async Task Nothing_to_reset_clears_the_attempt_without_closing_the_episode()
    {
        _client.ThenUsage(Usage.Blocked()).ThenConsume(ResetOutcome.NothingToReset);

        var report = await Run();

        Assert.Equal(ResetRunStatus.NothingToReset, report.Status);
        var state = _store.Load();
        Assert.Null(state.Pending);
        Assert.Null(state.LastResolvedLimitEventId);
    }

    [Fact]
    public async Task No_credit_from_backend_is_definitive()
    {
        _client.ThenUsage(Usage.Blocked()).ThenConsume(ResetOutcome.NoCredit);

        var report = await Run();

        Assert.Equal(ResetRunStatus.NoCredit, report.Status);
        Assert.Null(_store.Load().Pending);
    }

    [Fact]
    public async Task Policy_refusal_never_reaches_the_backend()
    {
        _client.ThenUsage(Usage.Blocked() with { ReachedType = "workspace_owner_credits_depleted" });

        var report = await Run(force: true);

        Assert.Equal(ResetRunStatus.NotOffered, report.Status);
        Assert.Equal(NoOfferReason.WorkspaceLimit, report.NoOfferReason);
        Assert.Empty(_client.ConsumeCalls);
    }

    [Fact]
    public async Task Automatic_mode_requires_an_explicit_backend_block()
    {
        _client.ThenUsage(Usage.Blocked(fiveHour: 100, allowed: null));

        var report = await Run(automatic: true);

        Assert.Equal(ResetRunStatus.NotOffered, report.Status);
        Assert.Empty(_client.ConsumeCalls);
    }

    [Fact]
    public async Task Automatic_mode_respects_the_daily_cap()
    {
        var state = new ResetState();
        state.AddHistory(new ResetAttemptRecord(Usage.Now.AddHours(-3), "k", "e", ResetOutcome.Reset, ResetRunStatus.Succeeded));
        _store.Save(state);
        _client.ThenUsage(Usage.Blocked());

        var report = await Run(automatic: true);

        Assert.Equal(ResetRunStatus.AutomaticCapReached, report.Status);
        Assert.Empty(_client.ConsumeCalls);
    }

    [Fact]
    public async Task Automatic_mode_never_resumes_an_unresolved_attempt()
    {
        _store.Save(new ResetState { Pending = new PendingResetAttempt("k", null, "e", Usage.Now) });
        _client.ThenUsage(Usage.Blocked());

        var report = await Run(automatic: true);

        Assert.Equal(ResetRunStatus.PendingAttemptNeedsUser, report.Status);
        Assert.Empty(_client.ConsumeCalls);
    }

    [Fact]
    public async Task Automatic_mode_cannot_force_past_the_episode_guard()
    {
        _options.Reset.CooldownSeconds = 0;
        _client.FallbackUsage = Usage.Blocked();
        _client.ThenUsage(Usage.Blocked()).ThenConsume(ResetOutcome.Reset);
        await Run();

        _client.ThenUsage(Usage.Blocked());
        var report = await Manager().ExecuteAsync(new ResetRequest(Force: true, Automatic: true), CancellationToken.None);

        Assert.Equal(ResetRunStatus.AlreadyHandled, report.Status);
        Assert.Single(_client.ConsumeCalls);
    }
}
