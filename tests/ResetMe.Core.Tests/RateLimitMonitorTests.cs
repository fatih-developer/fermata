using ResetMe.Core.Domain;
using ResetMe.Core.Monitoring;
using ResetMe.Core.Policies;
using ResetMe.Core.Ports;
using ResetMe.Core.Reset;

namespace ResetMe.Core.Tests;

public sealed class RateLimitMonitorTests : IDisposable
{
    private static readonly MonitorTiming Instant = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, [TimeSpan.Zero]);

    private readonly FakeCodexClient _client = new();
    private readonly FakeConnection _connection;
    private readonly FakeConnector _connector;
    private readonly InMemoryStateStore _store = new();
    private readonly RecordingObserver _observer = new();
    private readonly GuardOptions _options = Usage.FastOptions();
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(10));

    public RateLimitMonitorTests()
    {
        _connection = new FakeConnection(_client);
        _connector = new FakeConnector(_connection);
        _options.Reset.CooldownSeconds = 0;
    }

    public void Dispose() => _stop.Dispose();

    private async Task RunUntil(Func<RecordingObserver, bool> done, MonitorTiming? timing = null)
    {
        _observer.StopWhen(done, _stop);
        var monitor = new RateLimitMonitor(
            _connector,
            client => new ResetManager(client, _store, new FakeLock(), _options, new SteppingTimeProvider(Usage.Now)),
            _store,
            _observer,
            _options,
            timing ?? Instant,
            new SteppingTimeProvider(Usage.Now));

        await monitor.RunAsync(_stop.Token);
        Assert.False(_observer.TimedOut, "monitor did not reach the expected state in time");
    }

    [Fact]
    public async Task Healthy_usage_is_reported_and_nothing_is_consumed()
    {
        _client.FallbackUsage = Usage.Healthy(credits: 2);

        await RunUntil(o => o.UsageReads >= 3);

        Assert.Empty(_observer.Notices);
        Assert.Empty(_client.ConsumeCalls);
    }

    [Fact]
    public async Task Confirm_mode_asks_once_and_resets_on_yes()
    {
        _client.FallbackUsage = Usage.Healthy();
        _client.ThenUsage(Usage.Blocked()).ThenUsage(Usage.Blocked()) // light + detailed read
            .ThenUsage(Usage.Blocked())                               // ResetManager re-read under lock
            .ThenConsume(ResetOutcome.Reset);
        _observer.Answer = true;

        await RunUntil(o => o.Reports.Count == 1 && o.UsageReads >= 3);

        var notice = Assert.Single(_observer.Notices);
        Assert.Equal(LimitHandling.AskUser, notice.Handling);
        Assert.Equal(1, _observer.Questions);
        Assert.Single(_client.ConsumeCalls);
        Assert.Equal(ResetRunStatus.Succeeded, _observer.Reports[0].Status);
    }

    [Fact]
    public async Task Declined_episode_is_not_offered_again()
    {
        _client.FallbackUsage = Usage.Blocked();
        _observer.Answer = false;

        await RunUntil(o => o.UsageReads >= 5);

        Assert.Equal(1, _observer.Questions);
        Assert.Single(_observer.Notices);
        Assert.Empty(_client.ConsumeCalls);
    }

    [Fact]
    public async Task A_new_episode_is_offered_again_after_a_decline()
    {
        var first = Usage.Blocked();
        var second = Usage.Blocked() with { FiveHour = new UsageWindow(100, 300, Usage.Now.AddHours(6)) };
        _client.ThenUsage(first).ThenUsage(first).ThenUsage(first).ThenUsage(first);
        _client.FallbackUsage = second;
        _observer.Answer = false;

        await RunUntil(o => o.Questions >= 2);

        Assert.Equal(2, _observer.Notices.Select(n => n.Assessment.LimitEventId).Distinct().Count());
        Assert.Empty(_client.ConsumeCalls);
    }

    [Fact]
    public async Task Confirm_mode_only_reports_when_nobody_can_answer()
    {
        _observer.CanConfirm = false; // headless daemon / redirected stdin
        _client.FallbackUsage = Usage.Blocked();

        await RunUntil(o => o.UsageReads >= 3);

        Assert.Equal(LimitHandling.ReportOnly, Assert.Single(_observer.Notices).Handling);
        Assert.Equal(0, _observer.Questions);
        Assert.Empty(_client.ConsumeCalls);
    }

    [Fact]
    public async Task Manual_mode_only_reports()
    {
        _options.Mode = GuardMode.Manual;
        _client.FallbackUsage = Usage.Blocked();

        await RunUntil(o => o.UsageReads >= 3);

        Assert.Equal(LimitHandling.ReportOnly, Assert.Single(_observer.Notices).Handling);
        Assert.Equal(0, _observer.Questions);
        Assert.Empty(_client.ConsumeCalls);
    }

    [Fact]
    public async Task Automatic_mode_resets_without_asking()
    {
        _options.Mode = GuardMode.Automatic;
        _client.FallbackUsage = Usage.Healthy();
        _client.ThenUsage(Usage.Blocked()).ThenUsage(Usage.Blocked()).ThenUsage(Usage.Blocked())
            .ThenConsume(ResetOutcome.Reset);

        await RunUntil(o => o.Reports.Count == 1);

        Assert.Equal(LimitHandling.ResetAutomatically, Assert.Single(_observer.Notices).Handling);
        Assert.Equal(0, _observer.Questions);
        Assert.Single(_client.ConsumeCalls);
    }

    [Fact]
    public async Task Usage_is_read_right_after_a_reset_instead_of_after_the_blocked_interval()
    {
        // The blocked interval is an hour: a second read can only come from the post-reset re-read.
        var slowWhileBlocked = new MonitorTiming(TimeSpan.Zero, TimeSpan.FromHours(1), TimeSpan.Zero, [TimeSpan.Zero]);
        _options.Mode = GuardMode.Automatic;
        _client.FallbackUsage = Usage.Healthy();
        _client.ThenUsage(Usage.Blocked()).ThenUsage(Usage.Blocked()).ThenUsage(Usage.Blocked())
            .ThenConsume(ResetOutcome.Reset);

        await RunUntil(o => o.Reports.Count == 1 && o.UsageReads >= 2, slowWhileBlocked);
    }

    [Fact]
    public async Task No_credit_is_reported_without_asking()
    {
        _client.FallbackUsage = Usage.Blocked(credits: 0);

        await RunUntil(o => o.UsageReads >= 2);

        Assert.Equal(LimitHandling.NotOffered, Assert.Single(_observer.Notices).Handling);
        Assert.Equal(0, _observer.Questions);
    }

    [Fact]
    public async Task Episode_resolved_in_an_earlier_session_is_not_offered()
    {
        var blocked = Usage.Blocked();
        var episode = LimitEvaluator.Assess(blocked, _options, Usage.Now).LimitEventId;
        _store.Save(new ResetState { LastResolvedLimitEventId = episode });
        _client.FallbackUsage = blocked;

        await RunUntil(o => o.UsageReads >= 2);

        Assert.Equal(LimitHandling.AlreadyHandled, Assert.Single(_observer.Notices).Handling);
        Assert.Equal(0, _observer.Questions);
    }

    [Fact]
    public async Task Outage_is_reported_and_the_monitor_reconnects()
    {
        _connector.FailNext(2);
        _client.FallbackUsage = Usage.Healthy();

        await RunUntil(o => o.UsageReads >= 1);

        Assert.Equal(2, _observer.Outages);
        Assert.Equal(3, _connector.Attempts);
    }

    [Fact]
    public async Task Missing_login_is_reported_and_usage_is_not_read()
    {
        _connection.Authenticated = false;

        await RunUntil(o => o.AuthRequired >= 2);

        Assert.Equal(0, _observer.UsageReads);
    }

    [Fact]
    public async Task Push_signal_triggers_an_early_read()
    {
        // With an hour-long poll interval, a second read can only come from the push signal.
        var slow = new MonitorTiming(TimeSpan.FromHours(1), TimeSpan.FromHours(1), TimeSpan.FromHours(1), [TimeSpan.Zero]);
        _client.FallbackUsage = Usage.Healthy();
        _observer.OnFirstUsage = _connection.RaiseUsageChanged;

        await RunUntil(o => o.UsageReads >= 2, slow);
    }

    private sealed class FakeConnection(FakeCodexClient inner) : ICodexConnection
    {
        public event Action? UsageChanged;

        public event Action? AccountChanged;

        public bool Authenticated { get; set; } = true;

        public void RaiseUsageChanged() => UsageChanged?.Invoke();

        public void RaiseAccountChanged() => AccountChanged?.Invoke();

        public Task<CodexUsage> GetUsageAsync(bool includeCreditDetails, CancellationToken cancellationToken) =>
            inner.GetUsageAsync(includeCreditDetails, cancellationToken);

        public Task<AccountStatus> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AccountStatus(Authenticated, "chatgpt", "plus"));

        public Task<ResetOutcome> ConsumeResetAsync(string idempotencyKey, string? creditId, CancellationToken cancellationToken) =>
            inner.ConsumeResetAsync(idempotencyKey, creditId, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConnector(ICodexConnection connection) : ICodexConnector
    {
        private int _failuresLeft;

        public int Attempts { get; private set; }

        public void FailNext(int count) => _failuresLeft = count;

        public Task<ICodexConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            Attempts++;
            if (_failuresLeft > 0)
            {
                _failuresLeft--;
                throw new CodexTransientException("codex app-server not reachable");
            }

            return Task.FromResult(connection);
        }
    }

    private sealed class RecordingObserver : IMonitorObserver
    {
        private Func<RecordingObserver, bool> _done = _ => false;
        private CancellationTokenSource? _stop;

        public bool Answer { get; set; }

        public bool CanConfirm { get; set; } = true;

        public Action? OnFirstUsage { get; set; }

        public int UsageReads { get; private set; }

        public int Questions { get; private set; }

        public int Outages { get; private set; }

        public int AuthRequired { get; private set; }

        public List<LimitNotice> Notices { get; } = [];

        public List<ResetReport> Reports { get; } = [];

        public bool TimedOut => _stop?.IsCancellationRequested == true && !_done(this);

        public void StopWhen(Func<RecordingObserver, bool> done, CancellationTokenSource stop)
        {
            _done = done;
            _stop = stop;
        }

        public void OnUsage(CodexUsage usage, LimitAssessment assessment)
        {
            UsageReads++;
            if (UsageReads == 1)
            {
                OnFirstUsage?.Invoke();
            }

            Check();
        }

        public void OnLimitReached(LimitNotice notice)
        {
            Notices.Add(notice);
            Check();
        }

        public Task<bool> ConfirmResetAsync(LimitNotice notice, CancellationToken cancellationToken)
        {
            Questions++;
            Check();
            return Task.FromResult(Answer);
        }

        public void OnResetCompleted(ResetReport report)
        {
            Reports.Add(report);
            Check();
        }

        public void OnResetFailed(Exception error) => throw new InvalidOperationException("unexpected reset failure", error);

        public void OnAuthRequired()
        {
            AuthRequired++;
            Check();
        }

        public void OnUnavailable(Exception error, TimeSpan retryIn)
        {
            Outages++;
            Check();
        }

        private void Check()
        {
            if (_done(this))
            {
                _stop?.Cancel();
            }
        }
    }
}
