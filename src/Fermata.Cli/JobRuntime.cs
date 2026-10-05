using Fermata.Claude;
using Fermata.Codex.AppServer;
using Fermata.Codex.Jobs;
using Fermata.Core.Jobs;
using Fermata.Core.Policies;
using Fermata.Platform;
using Fermata.Platform.Codex;
using Fermata.Platform.Jobs;
using Microsoft.Extensions.Logging;

namespace Fermata.Cli;

/// <summary>Job wiring for the CLI and the daemon: store, providers and scheduler.</summary>
internal sealed class JobRuntime
{
    private JobRuntime(AppPaths paths, GuardOptions options)
    {
        Paths = paths;
        Options = options;
        Store = new JsonJobStore(paths.JobsDirectory);
        Lock = new FileResetLock(paths.JobsLockFile);
        ClaudeState = new ClaudeStateStore(paths);
    }

    public AppPaths Paths { get; }

    public GuardOptions Options { get; }

    public JsonJobStore Store { get; }

    public FileResetLock Lock { get; }

    public ClaudeStateStore ClaudeState { get; }

    public static JobRuntime Create(AppPaths paths, GuardOptions options) => new(paths, options);

    public static JobRuntime Load()
    {
        var runtime = GuardRuntime.Load();
        return new JobRuntime(runtime.Paths, runtime.Options);
    }

    public IJobProvider Provider(JobProviderKind kind) => Providers().First(p => p.Kind == kind);

    public IReadOnlyList<IJobProvider> Providers()
    {
        var options = Options;
        var statusStore = new JsonStatusSnapshotStore(Paths.StatusFile);
        var codex = new CodexJobProvider(
            new CodexJobProviderOptions
            {
                CodexHome = CodexHooksInstaller.DefaultCodexHome(),
                Executable = () => CodexLocator.Resolve(options.CodexExecutable),
                Approvals = options.Jobs.Codex.Approvals,
                Model = string.IsNullOrWhiteSpace(options.Jobs.Codex.Model) ? null : options.Jobs.Codex.Model,
            },
            () => statusStore.Load() is { } snapshot && snapshot.IsFresh(DateTimeOffset.UtcNow) ? QuotaSnapshot.FromCodex(snapshot) : null);
        var claude = new ClaudeJobProvider(
            new ClaudeCli(() => ClaudeLocator.Resolve(options.Jobs.Claude.Executable)),
            ClaudeState,
            () => new ClaudeJobProviderOptions { PermissionMode = options.Jobs.Claude.PermissionMode, Policy = options.Jobs.QuotaPolicy },
            TimeProvider.System);
        return [codex, claude];
    }

    public JobScheduler CreateScheduler() =>
        new(Store, Providers(), new CheckpointWriter(), () => Options.Jobs, TimeProvider.System, AppLogging.Factory.CreateLogger<JobScheduler>());
}
