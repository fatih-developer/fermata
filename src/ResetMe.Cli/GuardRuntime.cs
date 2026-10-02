using ResetMe.Codex.AppServer;
using ResetMe.Core.Policies;
using ResetMe.Platform;

namespace ResetMe.Cli;

/// <summary>Shared wiring for commands: paths, config, state and the Codex client.</summary>
internal sealed class GuardRuntime
{
    private GuardRuntime(AppPaths paths, ConfigLoadResult config)
    {
        Paths = paths;
        Config = config;
        StateStore = new JsonResetStateStore(paths.StateFile);
        Lock = new FileResetLock(paths.LockFile);
    }

    public AppPaths Paths { get; }

    public ConfigLoadResult Config { get; }

    public GuardOptions Options => Config.Options;

    public JsonResetStateStore StateStore { get; }

    public FileResetLock Lock { get; }

    public static GuardRuntime Load()
    {
        var paths = AppPaths.Default();
        paths.EnsureRoot();
        var config = new TomlConfigStore(paths.ConfigFile).Load();
        return new GuardRuntime(paths, config);
    }

    public Task<CodexAppServerClient> ConnectAsync(CancellationToken cancellationToken) =>
        CodexAppServerClient.StartAsync(
            new CodexClientOptions { Executable = Options.CodexExecutable },
            TimeProvider.System,
            cancellationToken);
}
