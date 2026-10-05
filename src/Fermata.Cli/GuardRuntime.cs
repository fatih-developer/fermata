using Fermata.Codex.AppServer;
using Fermata.Core.Policies;
using Fermata.Platform;

namespace Fermata.Cli;

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
        Migrate(paths);
        paths.EnsureRoot();
        var config = new TomlConfigStore(paths.ConfigFile).Load();
        AppLogging.Initialize(paths, config.Options.Logging);
        return new GuardRuntime(paths, config);
    }

    /// <summary>Copies ResetMe's data on the first run after the rename; refuses while ResetMe runs.</summary>
    public static void Migrate(AppPaths paths)
    {
        var result = LegacyMigration.ForDefaultPaths(paths).RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        switch (result.Outcome)
        {
            case MigrationOutcome.BlockedByRunningResetMe:
                throw new MigrationBlockedException(MigrationResult.BlockedMessage);
            case MigrationOutcome.Migrated:
                Console.Error.WriteLine(result.CopiedFiles.Count == 0
                    ? "Moved from ResetMe to Fermata (no settings to copy)."
                    : $"Moved from ResetMe to Fermata: copied {string.Join(", ", result.CopiedFiles)} to {paths.Root}.");
                if (result.LegacyAutostartWasEnabled)
                {
                    Console.Error.WriteLine("ResetMe's start-at-login entry was removed; start the Fermata app once to register it again.");
                }

                break;
        }
    }

    public Task<CodexAppServerClient> ConnectAsync(CancellationToken cancellationToken) =>
        CodexAppServerClient.StartAsync(
            new CodexClientOptions { Executable = Options.CodexExecutable },
            TimeProvider.System,
            cancellationToken,
            AppLogging.Factory.CreateLogger("Fermata.Codex"));
}

/// <summary>ResetMe still runs; Fermata must not start a second monitor next to it.</summary>
internal sealed class MigrationBlockedException(string message) : Exception(message);
