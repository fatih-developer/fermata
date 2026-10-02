using System.IO.Pipes;
using System.Text.Json.Nodes;
using ResetMe.Codex.AppServer;
using ResetMe.Codex.JsonRpc;
using ResetMe.Core.Domain;
using ResetMe.Core.Ports;

namespace ResetMe.Codex.Tests;

/// <summary>Drives <see cref="CodexAppServerClient"/> against an in-process fake app-server.</summary>
public sealed class AppServerClientTests : IAsyncDisposable
{
    private readonly FakeAppServer _server = new();

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private Task<CodexAppServerClient> Connect(TimeSpan? timeout = null) =>
        CodexAppServerClient.ConnectAsync(
            _server.ClientConnection,
            new CodexClientOptions
            {
                RequestTimeout = timeout ?? TimeSpan.FromSeconds(5),
                ConsumeTimeout = timeout ?? TimeSpan.FromSeconds(5),
            },
            TimeProvider.System,
            CancellationToken.None);

    [Fact]
    public async Task Initialize_handshake_identifies_the_client()
    {
        await using var client = await Connect();
        await _server.WaitForAsync("initialized");

        var init = _server.Snapshot().First(r => r.Method == "initialize");
        Assert.Equal("resetme", init.Params?["clientInfo"]?["name"]?.GetValue<string>());
        Assert.False(init.Params?["capabilities"]?["experimentalApi"]?.GetValue<bool>());
        Assert.Equal("0.159.3", client.CodexVersion);
        Assert.Contains(_server.Snapshot(), r => r.Method == "initialized" && r.Id is null);
    }

    [Fact]
    public async Task Background_reads_skip_credit_details_and_never_opt_into_experiments()
    {
        _server.Handle("account/rateLimits/read", _ => JsonNode.Parse(ProtocolMapperTests.RecordedRateLimits));
        await using var client = await Connect();

        var usage = await client.GetUsageAsync(includeCreditDetails: false, CancellationToken.None);

        var request = _server.Snapshot().Last(r => r.Method == "account/rateLimits/read");
        Assert.True(request.Params?["excludeResetCreditDetails"]?.GetValue<bool>());
        Assert.Null(request.Params?["supportsLunaReserve"]);
        Assert.Equal(2, usage.AvailableResetCount);
    }

    [Fact]
    public async Task Consume_sends_the_idempotency_key_and_credit()
    {
        _server.Handle("account/rateLimitResetCredit/consume", _ => new JsonObject { ["outcome"] = "reset" });
        await using var client = await Connect();

        var outcome = await client.ConsumeResetAsync("key-1", "credit-1", CancellationToken.None);

        var request = _server.Snapshot().Last(r => r.Method == "account/rateLimitResetCredit/consume");
        Assert.Equal("key-1", request.Params?["idempotencyKey"]?.GetValue<string>());
        Assert.Equal("credit-1", request.Params?["creditId"]?.GetValue<string>());
        Assert.Equal(ResetOutcome.Reset, outcome);
    }

    [Fact]
    public async Task Consume_without_credit_lets_the_backend_choose()
    {
        _server.Handle("account/rateLimitResetCredit/consume", _ => new JsonObject { ["outcome"] = "noCredit" });
        await using var client = await Connect();

        await client.ConsumeResetAsync("key-1", null, CancellationToken.None);

        var request = _server.Snapshot().Last(r => r.Method == "account/rateLimitResetCredit/consume");
        Assert.False(request.Params!.AsObject().ContainsKey("creditId"));
    }

    [Fact]
    public async Task Server_error_surfaces_as_rpc_exception()
    {
        _server.HandleError("account/read", -32000, "boom");
        await using var client = await Connect();

        var ex = await Assert.ThrowsAsync<JsonRpcException>(() => client.GetAccountAsync(CancellationToken.None));
        Assert.Equal(-32000, ex.Code);
    }

    [Fact]
    public async Task No_answer_is_transient_so_the_caller_can_retry_with_the_same_key()
    {
        await using var client = await Connect(TimeSpan.FromMilliseconds(300));
        _server.Ignore("account/rateLimitResetCredit/consume");

        await Assert.ThrowsAsync<CodexTransientException>(
            () => client.ConsumeResetAsync("key-1", null, CancellationToken.None));
    }

    [Fact]
    public async Task Server_exit_fails_pending_requests_as_transient()
    {
        _server.Ignore("account/rateLimits/read");
        await using var client = await Connect(TimeSpan.FromSeconds(10));

        var pending = client.GetUsageAsync(includeCreditDetails: false, CancellationToken.None);
        await _server.WaitForAsync("account/rateLimits/read");
        await _server.CloseAsync();

        await Assert.ThrowsAsync<CodexTransientException>(() => pending);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Rate_limit_push_raises_change_event()
    {
        await using var client = await Connect();
        var changed = new TaskCompletionSource();
        client.RateLimitsChanged += () => changed.TrySetResult();

        await _server.NotifyAsync("account/rateLimits/updated", new JsonObject { ["rateLimits"] = new JsonObject() });

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Server_requests_are_declined()
    {
        await using var client = await Connect();

        var reply = await _server.RequestFromServerAsync("item/commandExecution/requestApproval", new JsonObject());

        Assert.Equal(-32601, reply["error"]?["code"]?.GetValue<int>());
    }
}

internal sealed record ReceivedMessage(string Method, long? Id, JsonNode? Params);

/// <summary>Line-delimited JSON-RPC peer over in-process anonymous pipes.</summary>
internal sealed class FakeAppServer : IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _toClient = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _fromClient = new(PipeDirection.In);
    private readonly AnonymousPipeClientStream _clientIn;
    private readonly AnonymousPipeClientStream _clientOut;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly Dictionary<string, Func<JsonNode?, JsonNode?>> _handlers = [];
    private readonly Dictionary<string, (int Code, string Message)> _errors = [];
    private readonly HashSet<string> _ignored = [];
    private readonly Dictionary<string, TaskCompletionSource> _seen = [];
    private readonly Dictionary<long, TaskCompletionSource<JsonObject>> _serverRequests = [];
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _loop;
    private long _serverRequestId = 1000;

    public FakeAppServer()
    {
        _clientIn = new AnonymousPipeClientStream(PipeDirection.In, _toClient.ClientSafePipeHandle);
        _clientOut = new AnonymousPipeClientStream(PipeDirection.Out, _fromClient.ClientSafePipeHandle);
        _writer = new StreamWriter(_toClient) { AutoFlush = true };
        _reader = new StreamReader(_fromClient);

        ClientConnection = new JsonRpcConnection(new StreamReader(_clientIn), new StreamWriter(_clientOut) { AutoFlush = true });

        Handle("initialize", _ => new JsonObject
        {
            ["userAgent"] = "resetme/0.159.3 (Test; x86_64) test",
            ["codexHome"] = "/tmp/.codex",
            ["platformFamily"] = "test",
            ["platformOs"] = "test",
        });
        Handle("account/read", _ => new JsonObject { ["account"] = new JsonObject { ["type"] = "chatgpt", ["planType"] = "plus" } });

        _loop = Task.Run(LoopAsync);
    }

    public JsonRpcConnection ClientConnection { get; }

    private List<ReceivedMessage> Received { get; } = [];

    public IReadOnlyList<ReceivedMessage> Snapshot()
    {
        lock (Received)
        {
            return [.. Received];
        }
    }

    public void Handle(string method, Func<JsonNode?, JsonNode?> handler) => _handlers[method] = handler;

    public void HandleError(string method, int code, string message) => _errors[method] = (code, message);

    public void Ignore(string method) => _ignored.Add(method);

    public Task WaitForAsync(string method)
    {
        lock (_seen)
        {
            if (!_seen.TryGetValue(method, out var tcs))
            {
                _seen[method] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public Task NotifyAsync(string method, JsonNode parameters) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    public async Task<JsonObject> RequestFromServerAsync(string method, JsonNode parameters)
    {
        var id = Interlocked.Increment(ref _serverRequestId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_serverRequests)
        {
            _serverRequests[id] = tcs;
        }

        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task CloseAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            _toClient.Dispose();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Close the server side first so the client's read loop ends immediately.
        _toClient.Dispose();
        await ClientConnection.DisposeAsync();
        _clientOut.Dispose();
        _fromClient.Dispose();
        _clientIn.Dispose();
        await Task.WhenAny(_loop, Task.Delay(1000));
    }

    private async Task LoopAsync()
    {
        try
        {
            while (await _reader.ReadLineAsync() is { } line)
            {
                if (JsonNode.Parse(line) is not JsonObject msg)
                {
                    continue;
                }

                var method = msg["method"]?.GetValue<string>();
                var id = msg["id"]?.GetValue<long>();

                if (method is null && id is { } replyId)
                {
                    lock (_serverRequests)
                    {
                        if (_serverRequests.Remove(replyId, out var waiter))
                        {
                            waiter.TrySetResult(msg);
                        }
                    }

                    continue;
                }

                if (method is null)
                {
                    continue;
                }

                lock (Received)
                {
                    Received.Add(new ReceivedMessage(method, id, msg["params"]?.DeepClone()));
                }

                lock (_seen)
                {
                    if (!_seen.TryGetValue(method, out var tcs))
                    {
                        _seen[method] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    tcs.TrySetResult();
                }

                if (id is null || _ignored.Contains(method))
                {
                    continue;
                }

                JsonObject reply = _errors.TryGetValue(method, out var error)
                    ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message } }
                    : new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = id,
                        ["result"] = _handlers.TryGetValue(method, out var handler) ? handler(msg["params"]) : null,
                    };
                await WriteAsync(reply);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private async Task WriteAsync(JsonObject message)
    {
        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(message.ToJsonString());
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
