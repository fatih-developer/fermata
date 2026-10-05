using Fermata.Claude;
using Fermata.Codex.AppServer;
using Fermata.Codex.Jobs;
using Fermata.Core.Jobs;
using Fermata.Core.Policies;
using Fermata.Platform;
using Fermata.Platform.Codex;

namespace Fermata.Desktop.Services;

/// <summary>The Codex and Claude Code job providers, wired like the CLI's.</summary>
internal static class DesktopJobProviders
{
    public static IReadOnlyList<IJobProvider> Create(AppPaths paths, GuardOptions options)
    {
        var statusStore = new JsonStatusSnapshotStore(paths.StatusFile);
        return
        [
            new CodexJobProvider(
                new CodexJobProviderOptions
                {
                    CodexHome = CodexHooksInstaller.DefaultCodexHome(),
                    Executable = () => CodexLocator.Resolve(options.CodexExecutable),
                    Approvals = options.Jobs.Codex.Approvals,
                    Model = string.IsNullOrWhiteSpace(options.Jobs.Codex.Model) ? null : options.Jobs.Codex.Model,
                },
                () => statusStore.Load() is { } snapshot && snapshot.IsFresh(DateTimeOffset.UtcNow) ? QuotaSnapshot.FromCodex(snapshot) : null),
            new ClaudeJobProvider(
                new ClaudeCli(() => ClaudeLocator.Resolve(options.Jobs.Claude.Executable)),
                new ClaudeStateStore(paths),
                () => new ClaudeJobProviderOptions { PermissionMode = options.Jobs.Claude.PermissionMode, Policy = options.Jobs.QuotaPolicy },
                TimeProvider.System),
        ];
    }
}
