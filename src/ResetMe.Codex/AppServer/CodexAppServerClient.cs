using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResetMe.Codex.JsonRpc;
using ResetMe.Codex.Logging;
using ResetMe.Core.Domain;
using ResetMe.Core.Ports;

namespace ResetMe.Codex.AppServer;

public sealed record CodexClientOptions
{
    public string? Executable { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConsumeTimeout { get; init; } = TimeSpan.FromSeconds(45);
}

public sealed class CodexUnavailableException : Exception
{
    public CodexUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// <see cref="ICodexUsageClient"/> on top of the Codex App Server (docs/CODEX_INTEGRATION.md).
/// </summary>
public sealed class CodexAppServerClient : ICodexConnection
{
    internal const string ClientName = "resetme";

    private readonly JsonRpcConnection _connection;
    private readonly AppServerProcess? _process;
    private readonly CodexClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private CodexAppServerClient(
        JsonRpcConnection connection,
        AppServerProcess? process,
        CodexClientOptions options,
        TimeProvider time,
        ILogger? logger)
    {
        _connection = connection;
        _process = process;
        _options = options;
        _time = time;
        _logger = logger ?? NullLogger.Instance;
        _connection.NotificationReceived += OnNotification;
        _connection.Closed += OnClosed;
    }

    /// <summary>Raised when the server pushes <c>account/rateLimits/updated</c>; callers should re-read.</summary>
    public event Action? UsageChanged;

    /// <summary>Raised on <c>account/updated</c> (login, logout, plan change).</summary>
    public event Action? AccountChanged;

    /// <summary>Codex version parsed from the initialize user agent, if present.</summary>
    public string? CodexVersion { get; private set; }

    public string? CodexHome { get; private set; }

    public bool IsConnected => !_connection.IsClosed;

    public IReadOnlyList<string> RecentStderr => _process?.RecentStderr ?? [];

    /// <summary>Starts <c>codex app-server</c> and performs the initialize handshake.</summary>
    public static async Task<CodexAppServerClient> StartAsync(
        CodexClientOptions options,
        TimeProvider time,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        ArgumentNullException.ThrowIfNull(options);

        var executable = CodexLocator.Resolve(options.Executable)
            ?? throw new CodexUnavailableException(
                string.IsNullOrWhiteSpace(options.Executable)
                    ? "codex executable not found on PATH."
                    : $"codex executable not found at '{options.Executable}'.");

        CodexLog.Starting(logger, executable);
        AppServerProcess process;
        try
        {
            process = AppServerProcess.Start(executable);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new CodexUnavailableException($"Could not start '{executable} app-server'.", ex);
        }

        var connection = new JsonRpcConnection(process.Output, process.Input);
        var client = new CodexAppServerClient(connection, process, options, time, logger);
        try
        {
            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (Exception ex) when (ex is CodexTransientException or JsonRpcException)
        {
            var stderr = string.Join(Environment.NewLine, process.RecentStderr.TakeLast(5));
            CodexLog.InitializeFailed(logger, ex, stderr);
            await client.DisposeAsync().ConfigureAwait(false);
            throw new CodexUnavailableException(
                $"codex app-server did not complete initialize. {stderr}".Trim(), ex);
        }
    }

    /// <summary>For tests and custom transports: wraps an existing connection.</summary>
    internal static async Task<CodexAppServerClient> ConnectAsync(
        JsonRpcConnection connection,
        CodexClientOptions options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var client = new CodexAppServerClient(connection, null, options, time, null);
        await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    public async Task<CodexUsage> GetUsageAsync(bool includeCreditDetails, CancellationToken cancellationToken)
    {
        // supportsLunaReserve is deliberately never sent: it records experiment exposure.
        var parameters = new JsonObject { ["excludeResetCreditDetails"] = !includeCreditDetails };
        var result = await RequestAsync("account/rateLimits/read", parameters, _options.RequestTimeout, cancellationToken)
            .ConfigureAwait(false);
        return ProtocolMapper.MapUsage(result, _time.GetUtcNow());
    }

    public async Task<AccountStatus> GetAccountAsync(CancellationToken cancellationToken)
    {
        var parameters = new JsonObject { ["refreshToken"] = false };
        var result = await RequestAsync("account/read", parameters, _options.RequestTimeout, cancellationToken)
            .ConfigureAwait(false);
        return ProtocolMapper.MapAccount(result);
    }

    public async Task<ResetOutcome> ConsumeResetAsync(
        string idempotencyKey,
        string? creditId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var parameters = new JsonObject { ["idempotencyKey"] = idempotencyKey };
        if (creditId is not null)
        {
            parameters["creditId"] = creditId;
        }

        var result = await RequestAsync("account/rateLimitResetCredit/consume", parameters, _options.ConsumeTimeout, cancellationToken)
            .ConfigureAwait(false);
        return ProtocolMapper.MapOutcome(result);
    }

    public async ValueTask DisposeAsync()
    {
        _connection.NotificationReceived -= OnNotification;
        _connection.Closed -= OnClosed;
        _process?.Dispose(); // Closing stdio ends the read loop.
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await _connection.RequestAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
            CodexLog.RequestCompleted(_logger, method, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (Exception ex) when (ex is CodexTransientException or JsonRpcException)
        {
            CodexLog.RequestFailed(_logger, ex, method, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    private void OnClosed(Exception? error) => CodexLog.Closed(_logger, error);

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _connection.Start();

        var version = typeof(CodexAppServerClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var parameters = new JsonObject
        {
            ["clientInfo"] = new JsonObject
            {
                ["name"] = ClientName,
                ["title"] = "ResetMe",
                ["version"] = version.Split('+')[0],
            },
            ["capabilities"] = new JsonObject
            {
                ["experimentalApi"] = false,
                ["requestAttestation"] = false,
                ["optOutNotificationMethods"] = new JsonArray("remoteControl/status/changed"),
            },
        };

        var result = await RequestAsync("initialize", parameters, _options.RequestTimeout, cancellationToken)
            .ConfigureAwait(false);
        await _connection.NotifyAsync("initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);

        CodexHome = result?["codexHome"]?.GetValue<string>();
        CodexVersion = ParseVersion(result?["userAgent"]?.GetValue<string>());
        CodexLog.Connected(_logger, CodexVersion ?? "unknown");
    }

    /// <summary>"name/0.159.3 (Windows ...)" → "0.159.3".</summary>
    internal static string? ParseVersion(string? userAgent)
    {
        if (userAgent is null)
        {
            return null;
        }

        var slash = userAgent.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return null;
        }

        var rest = userAgent[(slash + 1)..];
        var end = rest.IndexOf(' ', StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    private void OnNotification(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "account/rateLimits/updated":
                UsageChanged?.Invoke();
                break;
            case "account/updated":
                AccountChanged?.Invoke();
                break;
        }
    }
}
