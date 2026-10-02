using ResetMe.Core.Ports;

namespace ResetMe.Codex.AppServer;

/// <summary>Starts a fresh <c>codex app-server</c> per connection.</summary>
public sealed class CodexAppServerConnector : ICodexConnector
{
    private readonly CodexClientOptions _options;
    private readonly TimeProvider _time;

    public CodexAppServerConnector(CodexClientOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
    }

    public async Task<ICodexConnection> ConnectAsync(CancellationToken cancellationToken) =>
        await CodexAppServerClient.StartAsync(_options, _time, cancellationToken).ConfigureAwait(false);
}
