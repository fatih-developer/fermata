using Microsoft.Extensions.Logging;

namespace ResetMe.Core.Logging;

/// <summary>Reset flow events (1xx).</summary>
internal static partial class ResetLog
{
    [LoggerMessage(100, LogLevel.Information, "Reset run started (force={Force}, automatic={Automatic})")]
    public static partial void RunStarted(ILogger logger, bool force, bool automatic);

    [LoggerMessage(101, LogLevel.Information, "Reset not attempted: {Status} ({Reason})")]
    public static partial void NotAttempted(ILogger logger, string status, string reason);

    [LoggerMessage(102, LogLevel.Information, "Reset attempt started for episode {LimitEventId} with key {IdempotencyKey}, credit {CreditId}")]
    public static partial void AttemptStarted(ILogger logger, string limitEventId, string idempotencyKey, string creditId);

    [LoggerMessage(103, LogLevel.Information, "Resuming unresolved attempt {IdempotencyKey} started at {StartedAt:o}")]
    public static partial void Resuming(ILogger logger, string idempotencyKey, DateTimeOffset startedAt);

    [LoggerMessage(104, LogLevel.Warning, "Consume try {Try}/{Tries} failed transiently; retrying with the same key")]
    public static partial void ConsumeRetry(ILogger logger, Exception error, int @try, int tries);

    [LoggerMessage(105, LogLevel.Warning, "Consume outcome unknown after {Tries} tries; verifying by reading usage")]
    public static partial void ConsumeUnknown(ILogger logger, Exception error, int tries);

    [LoggerMessage(106, LogLevel.Information, "Consume outcome {Outcome} for key {IdempotencyKey}")]
    public static partial void ConsumeOutcome(ILogger logger, string outcome, string idempotencyKey);

    [LoggerMessage(107, LogLevel.Information, "Reset verified: Codex usable again (5h {FiveHour}%, weekly {Weekly}%, credits left {Credits})")]
    public static partial void Verified(ILogger logger, double? fiveHour, double? weekly, long credits);

    [LoggerMessage(108, LogLevel.Warning, "Reset unconfirmed for key {IdempotencyKey} (outcome {Outcome}); pending attempt kept={PendingKept}")]
    public static partial void Unconfirmed(ILogger logger, string idempotencyKey, string outcome, bool pendingKept);

    [LoggerMessage(109, LogLevel.Debug, "Verification read failed transiently")]
    public static partial void VerifyReadFailed(ILogger logger, Exception error);
}

/// <summary>Monitor events (2xx).</summary>
internal static partial class MonitorLog
{
    [LoggerMessage(200, LogLevel.Information, "Connected to Codex; monitoring (mode {Mode}, poll {PollSeconds}s)")]
    public static partial void Connected(ILogger logger, string mode, double pollSeconds);

    [LoggerMessage(201, LogLevel.Warning, "Codex unavailable; retrying in {RetrySeconds}s")]
    public static partial void Unavailable(ILogger logger, Exception error, double retrySeconds);

    [LoggerMessage(202, LogLevel.Warning, "Codex is not logged in")]
    public static partial void AuthRequired(ILogger logger);

    [LoggerMessage(203, LogLevel.Debug, "Usage 5h={FiveHour}% weekly={Weekly}% allowed={Allowed} credits={Credits}")]
    public static partial void Usage(ILogger logger, double? fiveHour, double? weekly, bool? allowed, long credits);

    [LoggerMessage(204, LogLevel.Information, "Limit episode {LimitEventId}: exhausted=[{Windows}] handling={Handling} reason={Reason}")]
    public static partial void Episode(ILogger logger, string limitEventId, string windows, string handling, string reason);

    [LoggerMessage(205, LogLevel.Information, "User {Decision} the reset offer for episode {LimitEventId}")]
    public static partial void Decision(ILogger logger, string decision, string limitEventId);

    [LoggerMessage(206, LogLevel.Information, "Reset run finished: {Status}")]
    public static partial void ResetFinished(ILogger logger, string status);

    [LoggerMessage(207, LogLevel.Error, "Reset run failed")]
    public static partial void ResetFailed(ILogger logger, Exception error);

    [LoggerMessage(208, LogLevel.Debug, "Push signal: {Signal}")]
    public static partial void Push(ILogger logger, string signal);

    [LoggerMessage(209, LogLevel.Information, "Codex usable again")]
    public static partial void Recovered(ILogger logger);

    [LoggerMessage(210, LogLevel.Information, "Near limit: {Window} window at {UsedPercent}% (threshold {Threshold}%)")]
    public static partial void NearLimit(ILogger logger, string window, int threshold, double usedPercent);
}
