using Microsoft.Extensions.Logging;

namespace Fermata.Codex.Logging;

/// <summary>Codex integration events (3xx). Request parameters and results are never logged.</summary>
internal static partial class CodexLog
{
    [LoggerMessage(300, LogLevel.Information, "Starting {Executable} app-server")]
    public static partial void Starting(ILogger logger, string executable);

    [LoggerMessage(301, LogLevel.Information, "Connected to Codex app-server {Version}")]
    public static partial void Connected(ILogger logger, string version);

    [LoggerMessage(302, LogLevel.Warning, "codex app-server initialize failed; stderr tail: {Stderr}")]
    public static partial void InitializeFailed(ILogger logger, Exception error, string stderr);

    [LoggerMessage(303, LogLevel.Debug, "{Method} completed in {ElapsedMs} ms")]
    public static partial void RequestCompleted(ILogger logger, string method, long elapsedMs);

    [LoggerMessage(304, LogLevel.Warning, "{Method} failed after {ElapsedMs} ms")]
    public static partial void RequestFailed(ILogger logger, Exception error, string method, long elapsedMs);

    [LoggerMessage(305, LogLevel.Warning, "Connection to codex app-server closed")]
    public static partial void Closed(ILogger logger, Exception? error);
}
