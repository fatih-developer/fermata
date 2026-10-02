using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResetMe.Core.Policies;
using ResetMe.Platform;
using ResetMe.Platform.Logging;

namespace ResetMe.Cli;

/// <summary>
/// Process-wide logger factory for the CLI composition root. Logs go to the file only;
/// the terminal is the user interface, not a log sink.
/// </summary>
internal static partial class AppLogging
{
    private static ILoggerFactory? _factory;

    public static ILoggerFactory Factory => _factory ?? NullLoggerFactory.Instance;

    public static void Initialize(AppPaths paths, LoggingOptions options)
    {
        if (_factory is not null)
        {
            return;
        }

        var provider = new FileLoggerProvider(paths.LogDirectory, options.Level, options.RetentionDays, TimeProvider.System);
        _factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(options.Level)
            .AddProvider(provider));
    }

    /// <summary>Starts logging with the configured level; falls back to defaults if config is unreadable.</summary>
    public static void InitializeFromDefaults()
    {
        var paths = AppPaths.Default();
        paths.EnsureRoot();
        LoggingOptions options;
        try
        {
            options = new TomlConfigStore(paths.ConfigFile).Load().Options.Logging;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Tomlyn.TomlException)
        {
            options = new LoggingOptions();
        }

        Initialize(paths, options);
    }

    /// <summary>Flushes and closes the log file. Call once before the process exits.</summary>
    public static void Shutdown()
    {
        _factory?.Dispose();
        _factory = null;
    }

    [LoggerMessage(1, LogLevel.Information, "resetme {Command} started (version {Version}, os {Os})")]
    public static partial void CommandStarted(ILogger logger, string command, string version, string os);

    [LoggerMessage(2, LogLevel.Information, "resetme {Command} finished with exit code {ExitCode}")]
    public static partial void CommandFinished(ILogger logger, string command, int exitCode);

    [LoggerMessage(3, LogLevel.Error, "resetme {Command} failed: {ErrorCode}")]
    public static partial void CommandFailed(ILogger logger, Exception error, string command, string errorCode);
}
