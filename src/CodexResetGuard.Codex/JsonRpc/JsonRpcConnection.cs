using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexResetGuard.Core.Ports;

namespace CodexResetGuard.Codex.JsonRpc;

/// <summary>Error object returned by the server for a request.</summary>
public sealed class JsonRpcException : Exception
{
    public JsonRpcException(string method, int code, string message)
        : base($"{method} failed ({code}): {message}")
    {
        Method = method;
        Code = code;
    }

    public string Method { get; }

    public int Code { get; }
}

/// <summary>
/// Newline-delimited JSON-RPC 2.0 over a pair of text streams (the app-server stdio transport).
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private const int MethodNotFound = -32601;

    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _readLoop;
    private long _nextId;
    private volatile bool _closed;

    public JsonRpcConnection(TextReader reader, TextWriter writer)
    {
        _reader = reader;
        _writer = writer;
    }

    /// <summary>Raised on the read loop for every server notification.</summary>
    public event Action<string, JsonNode?>? NotificationReceived;

    /// <summary>Raised once when the stream ends or fails.</summary>
    public event Action<Exception?>? Closed;

    public bool IsClosed => _closed;

    public void Start() => _readLoop ??= Task.Run(ReadLoopAsync);

    public async Task<JsonNode?> RequestAsync(
        string method,
        JsonNode? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_closed)
        {
            throw new CodexTransientException($"Connection closed before '{method}' was sent.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var pending = new Pending(method);
        _pending[id] = pending;

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        try
        {
            await WriteAsync(message, cancellationToken).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new CodexTransientException($"'{method}' timed out after {timeout.TotalSeconds:0}s.", ex);
        }
        catch (IOException ex)
        {
            throw new CodexTransientException($"'{method}' failed: connection lost.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return WriteAsync(message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        MarkClosed(null);
        if (_readLoop is not null)
        {
            // The loop ends when the underlying stream closes; the owner closes it.
            await Task.WhenAny(_readLoop, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var line = message.ToJsonString();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length > 0)
                {
                    await DispatchAsync(line).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            failure = ex;
        }

        MarkClosed(failure);
    }

    private async Task DispatchAsync(string line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return; // Not a protocol message.
        }

        if (node is not JsonObject obj)
        {
            return;
        }

        var method = obj["method"]?.GetValue<string>();
        var idNode = obj["id"];

        if (method is null && idNode is not null)
        {
            HandleResponse(obj, idNode);
        }
        else if (method is not null && idNode is not null)
        {
            // Server-to-client request (approvals etc.). This client never runs turns, so decline.
            await ReplyMethodNotFoundAsync(idNode, method).ConfigureAwait(false);
        }
        else if (method is not null)
        {
            NotificationReceived?.Invoke(method, obj["params"]);
        }
    }

    private void HandleResponse(JsonObject obj, JsonNode idNode)
    {
        if (idNode.GetValueKind() != JsonValueKind.Number
            || !_pending.TryGetValue(idNode.GetValue<long>(), out var pending))
        {
            return;
        }

        if (obj["error"] is JsonObject error)
        {
            var code = error["code"]?.GetValue<int>() ?? 0;
            var message = error["message"]?.GetValue<string>() ?? "unknown error";
            pending.Completion.TrySetException(new JsonRpcException(pending.Method, code, message));
        }
        else
        {
            pending.Completion.TrySetResult(obj["result"]?.DeepClone());
        }
    }

    private async Task ReplyMethodNotFoundAsync(JsonNode idNode, string method)
    {
        var reply = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNode.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = MethodNotFound,
                ["message"] = $"codex-reset-guard does not handle '{method}'.",
            },
        };

        try
        {
            await WriteAsync(reply, _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void MarkClosed(Exception? failure)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        foreach (var (_, pending) in _pending)
        {
            pending.Completion.TrySetException(
                new CodexTransientException($"Connection closed while waiting for '{pending.Method}'.", failure));
        }

        Closed?.Invoke(failure);
    }

    private sealed class Pending(string method)
    {
        public string Method { get; } = method;

        public TaskCompletionSource<JsonNode?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
