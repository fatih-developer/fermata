namespace ResetMe.Core.Ports;

/// <summary>A live session with Codex that can also push change signals.</summary>
public interface ICodexConnection : ICodexUsageClient, IAsyncDisposable
{
    /// <summary>Usage may have changed (push); the receiver should re-read.</summary>
    event Action? UsageChanged;

    /// <summary>Login, logout or plan changed.</summary>
    event Action? AccountChanged;
}

/// <summary>Opens connections; a monitor reconnects through it after failures.</summary>
public interface ICodexConnector
{
    Task<ICodexConnection> ConnectAsync(CancellationToken cancellationToken);
}
