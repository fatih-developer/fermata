using Microsoft.Extensions.Logging;
using Fermata.Core.Ports;

namespace Fermata.Codex.AppServer;

/// <summary>Starts a fresh <c>codex app-server</c> per connection.</summary>
public sealed class CodexAppServerConnector : ICodexConnector
{
    private readonly CodexClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;

    public CodexAppServerConnector(CodexClientOptions options, TimeProvider time, ILogger? logger = null)
    {
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<ICodexConnection> ConnectAsync(CancellationToken cancellationToken) =>
        await CodexAppServerClient.StartAsync(_options, _time, cancellationToken, _logger).ConfigureAwait(false);
}
