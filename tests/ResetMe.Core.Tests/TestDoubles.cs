using ResetMe.Core.Domain;
using ResetMe.Core.Policies;
using ResetMe.Core.Ports;
using ResetMe.Core.Reset;

namespace ResetMe.Core.Tests;

internal static class Usage
{
    public static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    public static CodexUsage Blocked(double fiveHour = 100, double weekly = 60, long credits = 1, bool? allowed = false) => new()
    {
        AccountId = "acct-1",
        UsageAllowed = allowed,
        ReachedType = "rate_limit_reached",
        FiveHour = new UsageWindow(fiveHour, 300, Now.AddHours(2)),
        Weekly = new UsageWindow(weekly, 10080, Now.AddDays(3)),
        AvailableResetCount = credits,
        ResetCreditsReported = true,
        Credits = credits > 0
            ? [new ResetCredit("credit-a", ResetCreditStatus.Available, Now.AddDays(-5), Now.AddDays(20), "Full reset")]
            : [],
        ReadAt = Now,
    };

    public static CodexUsage Healthy(long credits = 0) => Blocked(fiveHour: 0, weekly: 10, credits: credits, allowed: true);

    /// <summary>Options with every delay at zero so tests never sleep.</summary>
    public static GuardOptions FastOptions()
    {
        var options = new GuardOptions();
        options.Reset.VerifyAfterSeconds = 0;
        options.Reset.VerifyIntervalSeconds = 0;
        options.Reset.ConsumeRetryDelaySeconds = 0;
        options.Reset.VerifyTimeoutSeconds = 10;
        return options;
    }
}

/// <summary>Advances one second on every read so deadline loops always terminate.</summary>
internal sealed class SteppingTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        var now = _now;
        _now = _now.AddSeconds(1);
        return now;
    }

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class FakeCodexClient : ICodexUsageClient
{
    private readonly Queue<Func<CodexUsage>> _usage = new();
    private readonly Queue<Func<ResetOutcome>> _consume = new();

    /// <summary>Returned once the scripted usage queue is empty.</summary>
    public CodexUsage FallbackUsage { get; set; } = Usage.Healthy();

    public List<(string Key, string? CreditId)> ConsumeCalls { get; } = [];

    public Action<string>? OnConsume { get; set; }

    public FakeCodexClient ThenUsage(CodexUsage usage)
    {
        _usage.Enqueue(() => usage);
        return this;
    }

    public FakeCodexClient ThenConsume(ResetOutcome outcome)
    {
        _consume.Enqueue(() => outcome);
        return this;
    }

    public FakeCodexClient ThenConsumeTimesOut()
    {
        _consume.Enqueue(() => throw new CodexTransientException("timeout"));
        return this;
    }

    public Task<CodexUsage> GetUsageAsync(bool includeCreditDetails, CancellationToken cancellationToken) =>
        Task.FromResult(_usage.Count > 0 ? _usage.Dequeue()() : FallbackUsage);

    public Task<AccountStatus> GetAccountAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AccountStatus(true, "chatgpt", "plus"));

    public Task<ResetOutcome> ConsumeResetAsync(string idempotencyKey, string? creditId, CancellationToken cancellationToken)
    {
        ConsumeCalls.Add((idempotencyKey, creditId));
        OnConsume?.Invoke(idempotencyKey);
        if (_consume.Count == 0)
        {
            throw new InvalidOperationException("Unexpected consume call.");
        }

        return Task.FromResult(_consume.Dequeue()());
    }
}

internal sealed class InMemoryStateStore : IResetStateStore
{
    private string? _snapshot;

    public int SaveCount { get; private set; }

    // Round-trips through a copy so tests observe only what was actually saved.
    public ResetState Load() => _snapshot is null ? new ResetState() : Clone(_snapshot);

    public void Save(ResetState state)
    {
        SaveCount++;
        _snapshot = System.Text.Json.JsonSerializer.Serialize(state);
    }

    private static ResetState Clone(string json) => System.Text.Json.JsonSerializer.Deserialize<ResetState>(json)!;
}

internal sealed class FakeLock : IResetLock
{
    public bool Busy { get; set; }

    public IDisposable? TryAcquire() => Busy ? null : new Handle();

    private sealed class Handle : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
