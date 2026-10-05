using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Fermata.Codex.AppServer;

/// <summary>
/// Connects to the shared local app-server daemon (<c>codex app-server daemon</c>), where Codex TUI
/// sessions run. Its control socket is a Unix domain socket (AF_UNIX also on Windows) speaking
/// WebSocket; each text message is one JSON-RPC message.
/// </summary>
public static class DaemonTransport
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHandshakeBytes = 16 * 1024;

    /// <summary><c>$CODEX_HOME/app-server-control/app-server-control.sock</c>.</summary>
    public static string DefaultSocketPath(string codexHome) =>
        Path.Combine(codexHome, "app-server-control", "app-server-control.sock");

    public static async Task<WebSocket> ConnectAsync(string socketPath, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        NetworkStream? stream = null;
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
            stream = new NetworkStream(socket, ownsSocket: true);

            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request = "GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                + $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken).ConfigureAwait(false);

            var response = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
            var expected = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
            if (!response.StartsWith("HTTP/1.1 101", StringComparison.Ordinal)
                || !response.Contains(expected, StringComparison.Ordinal))
            {
                throw new CodexUnavailableException($"The Codex daemon refused the connection: {response.Split('\r')[0]}");
            }

            return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = false,
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                socket.Dispose();
            }

            throw;
        }
    }

    // Byte by byte so no WebSocket frame bytes are consumed with the headers.
    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(256);
        var one = new byte[1];
        while (buffer.Count < MaxHandshakeBytes)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new CodexUnavailableException("The Codex daemon closed the connection during the handshake.");
            }

            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n')
            {
                return Encoding.ASCII.GetString([.. buffer]);
            }
        }

        throw new CodexUnavailableException("The Codex daemon sent an oversized handshake.");
    }
}

/// <summary>Each WebSocket text message as one "line" for <see cref="JsonRpc.JsonRpcConnection"/>.</summary>
internal sealed class WebSocketLineReader(WebSocket socket) : TextReader
{
    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                throw new IOException(ex.Message, ex); // JsonRpcConnection treats IOException as "connection lost".
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                // Binary frames are not part of the protocol; skip them like blank lines.
                return result.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(message.ToArray()) : "";
            }
        }
    }

    public override string? ReadLine() => ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
}

/// <summary>Sends each written line as one WebSocket text message.</summary>
internal sealed class WebSocketLineWriter(WebSocket socket) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;

    public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(buffer.ToArray()), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Write(char value) => throw new NotSupportedException("Use WriteLineAsync: one call is one message.");
}
