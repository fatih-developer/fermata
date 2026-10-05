using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json.Nodes;
using Fermata.Codex.JsonRpc;

namespace Fermata.Codex.AppServer;

/// <summary><c>ThreadStatus</c>: notLoaded | idle | systemError | active (with flags).</summary>
public sealed record CodexThreadStatus(string Type, IReadOnlyList<string> ActiveFlags)
{
    public bool WaitingOnApproval => ActiveFlags.Contains("waitingOnApproval");

    public bool WaitingOnUserInput => ActiveFlags.Contains("waitingOnUserInput");
}

public sealed record CodexThreadInfo(string Id, CodexThreadStatus Status, string? Cwd, string? Preview, long? UpdatedAt);

/// <summary><c>ThreadGoalStatus</c>: active | paused | blocked | usageLimited | budgetLimited | complete.</summary>
public sealed record CodexGoal(string ThreadId, string Objective, string Status);

/// <summary>
/// One short connection to the shared app-server daemon (where Codex TUI sessions and Fermata jobs
/// run). Fermata never starts turns itself: it starts threads and sets goals, and the daemon runs
/// the turns. It also unsubscribes from threads it starts, so approval requests go to the user's
/// Codex client and never to this short-lived connection.
/// </summary>
public sealed class CodexDaemonClient : IAsyncDisposable
{
    private readonly JsonRpcConnection _connection;
    private readonly WebSocket? _socket;
    private readonly TimeSpan _timeout;

    internal CodexDaemonClient(JsonRpcConnection connection, WebSocket? socket, TimeSpan timeout)
    {
        _connection = connection;
        _socket = socket;
        _timeout = timeout;
    }

    /// <summary>Connects and initializes. Throws <see cref="CodexUnavailableException"/> when no daemon answers.</summary>
    public static async Task<CodexDaemonClient> ConnectAsync(string codexHome, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // No File.Exists check: AF_UNIX socket files are reparse points on Windows. A missing or
        // stale socket simply fails to connect.
        WebSocket socket;
        try
        {
            socket = await DaemonTransport.ConnectAsync(DaemonTransport.DefaultSocketPath(codexHome), cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or ArgumentOutOfRangeException)
        {
            // No daemon, a stale socket file, or a path too long for AF_UNIX (108 chars).
            throw new CodexUnavailableException($"No Codex daemon is reachable: {ex.Message}");
        }

        var connection = new JsonRpcConnection(new WebSocketLineReader(socket), new WebSocketLineWriter(socket));
        var client = new CodexDaemonClient(connection, socket, timeout);
        try
        {
            await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Over an already connected stream (tests).</summary>
    internal static async Task<CodexDaemonClient> OverAsync(JsonRpcConnection connection, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var client = new CodexDaemonClient(connection, null, timeout);
        await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _connection.Start();
        await RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject
            {
                ["name"] = CodexAppServerClient.ClientName,
                ["title"] = null,
                ["version"] = typeof(CodexDaemonClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0",
            },
            ["capabilities"] = new JsonObject { ["experimentalApi"] = false, ["requestAttestation"] = false },
        }, cancellationToken).ConfigureAwait(false);
        await _connection.NotifyAsync("initialized", new JsonObject(), cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken) =>
        _connection.RequestAsync(method, parameters, _timeout, cancellationToken);

    public async Task<IReadOnlyList<string>> LoadedThreadsAsync(CancellationToken cancellationToken)
    {
        var threads = new List<string>();
        string? cursor = null;
        do
        {
            var parameters = new JsonObject();
            if (cursor is not null)
            {
                parameters["cursor"] = cursor;
            }

            var page = await RequestAsync("thread/loaded/list", parameters, cancellationToken).ConfigureAwait(false);
            threads.AddRange((page?["data"] as JsonArray ?? []).Select(id => id?.GetValue<string>()).OfType<string>());
            cursor = page?["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null && threads.Count < 500);

        return threads;
    }

    /// <summary>Most recently updated threads (interactive sources), newest first.</summary>
    public async Task<IReadOnlyList<CodexThreadInfo>> RecentThreadsAsync(int limit, CancellationToken cancellationToken)
    {
        var page = await RequestAsync("thread/list", new JsonObject
        {
            ["limit"] = limit,
            ["sortKey"] = "updated_at",
            ["sortDirection"] = "desc",
            ["useStateDbOnly"] = true,
        }, cancellationToken).ConfigureAwait(false);
        return (page?["data"] as JsonArray ?? []).OfType<JsonObject>().Select(ParseThread).ToList();
    }

    public async Task<CodexThreadInfo> ReadThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        var result = await RequestAsync("thread/read", new JsonObject { ["threadId"] = threadId }, cancellationToken).ConfigureAwait(false);
        return result?["thread"] is JsonObject thread
            ? ParseThread(thread)
            : throw new CodexProtocolException($"thread/read returned no thread for {threadId}.");
    }

    public async Task<CodexGoal?> GetGoalAsync(string threadId, CancellationToken cancellationToken)
    {
        var result = await RequestAsync("thread/goal/get", new JsonObject { ["threadId"] = threadId }, cancellationToken).ConfigureAwait(false);
        return ParseGoal(threadId, result?["goal"]);
    }

    public async Task<CodexGoal?> SetGoalAsync(string threadId, string? objective, string? status, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject { ["threadId"] = threadId };
        if (objective is not null)
        {
            parameters["objective"] = objective;
        }

        if (status is not null)
        {
            parameters["status"] = status;
        }

        var result = await RequestAsync("thread/goal/set", parameters, cancellationToken).ConfigureAwait(false);
        return ParseGoal(threadId, result?["goal"]);
    }

    /// <summary>Starts a thread in the daemon and unsubscribes from it (see the class remarks).</summary>
    public async Task<string> StartThreadAsync(string cwd, string sandbox, string approvalsReviewer, string? model, CancellationToken cancellationToken)
    {
        var parameters = new JsonObject
        {
            ["cwd"] = cwd,
            ["sandbox"] = sandbox,
            ["approvalsReviewer"] = approvalsReviewer,
        };
        if (!string.IsNullOrEmpty(model))
        {
            parameters["model"] = model;
        }

        var result = await RequestAsync("thread/start", parameters, cancellationToken).ConfigureAwait(false);
        var id = result?["thread"]?["id"]?.GetValue<string>()
            ?? throw new CodexProtocolException("thread/start returned no thread id.");
        await UnsubscribeAsync(id, cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>Loads a thread that is not loaded (after a daemon restart), then unsubscribes again.</summary>
    public async Task LoadThreadAsync(string threadId, CancellationToken cancellationToken)
    {
        await RequestAsync("thread/resume", new JsonObject { ["threadId"] = threadId, ["excludeTurns"] = true }, cancellationToken).ConfigureAwait(false);
        await UnsubscribeAsync(threadId, cancellationToken).ConfigureAwait(false);
    }

    public Task UnsubscribeAsync(string threadId, CancellationToken cancellationToken) =>
        RequestAsync("thread/unsubscribe", new JsonObject { ["threadId"] = threadId }, cancellationToken);

    public async Task<bool> LastTurnHitUsageLimitAsync(string threadId, CancellationToken cancellationToken)
    {
        var turns = await RequestAsync("thread/turns/list", new JsonObject
        {
            ["threadId"] = threadId,
            ["limit"] = 1,
            ["sortDirection"] = "desc",
            ["itemsView"] = "notLoaded",
        }, cancellationToken).ConfigureAwait(false);
        var last = (turns?["data"] as JsonArray)?.FirstOrDefault();
        var errorInfo = last?["error"]?["codexErrorInfo"];
        return last?["status"]?.GetValue<string>() == "failed"
            && errorInfo is JsonValue value
            && value.GetValue<string>() == "usageLimitExceeded";
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket is not null)
        {
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        _socket?.Dispose();
    }

    internal static CodexThreadInfo ParseThread(JsonObject thread)
    {
        var status = thread["status"];
        var flags = (status?["activeFlags"] as JsonArray ?? []).Select(f => f?.GetValue<string>()).OfType<string>().ToList();
        var cwd = thread["cwd"]?.GetValue<string>()
            ?? (thread["environments"] as JsonArray)?.FirstOrDefault()?["cwd"]?.GetValue<string>();
        return new CodexThreadInfo(
            thread["id"]?.GetValue<string>() ?? "",
            new CodexThreadStatus(status?["type"]?.GetValue<string>() ?? "notLoaded", flags),
            cwd,
            thread["preview"]?.GetValue<string>(),
            thread["updatedAt"] is JsonValue updated && updated.TryGetValue<long>(out var at) ? at : null);
    }

    private static CodexGoal? ParseGoal(string threadId, JsonNode? goal) =>
        goal is JsonObject obj
            ? new CodexGoal(threadId, obj["objective"]?.GetValue<string>() ?? "", obj["status"]?.GetValue<string>() ?? "")
            : null;
}
