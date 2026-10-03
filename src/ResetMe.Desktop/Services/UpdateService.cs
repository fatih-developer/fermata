using ResetMe.Platform.Updates;

namespace ResetMe.Desktop.Services;

/// <summary>Self-update operations the desktop host needs (faked in tests).</summary>
public interface IUpdateService
{
    /// <summary>False for development builds: they are never replaced by an update.</summary>
    bool CanInstall { get; }

    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads, verifies and installs; the app must exit afterwards (it is relaunched).</summary>
    Task<ApplyResult> InstallAsync(UpdateCheckResult check, IProgress<string> progress, CancellationToken cancellationToken);
}

public sealed class GitHubUpdateService : IUpdateService, IDisposable
{
    private readonly HttpClient _http = new();
    private readonly InstallLayout? _layout = InstallLayout.Detect(AppContext.BaseDirectory);

    public bool CanInstall => _layout is not null;

    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken) =>
        new UpdateChecker(_http).CheckAsync(
            UpdateChecker.CurrentVersion(),
            UpdateChecker.PackageNameFor(UpdateChecker.CurrentRuntimeIdentifier()),
            cancellationToken);

    public async Task<ApplyResult> InstallAsync(UpdateCheckResult check, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var layout = _layout ?? throw new UpdateException("This copy of ResetMe is a development build and is never replaced.");
        var staged = await new UpdateInstaller(_http).DownloadAndStageAsync(check, layout, progress, cancellationToken).ConfigureAwait(false);
        return UpdateInstaller.Apply(staged, layout, [Environment.ProcessId], relaunchDesktop: true);
    }

    public void Dispose() => _http.Dispose();
}
