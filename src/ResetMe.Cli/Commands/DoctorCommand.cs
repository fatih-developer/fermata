using ResetMe.Codex.AppServer;
using ResetMe.Core.Ports;
using ResetMe.Platform;

namespace ResetMe.Cli.Commands;

/// <summary>Installation and connectivity checks (PRD FR-10).</summary>
internal static class DoctorCommand
{
    /// <summary>Versions the app-server protocol mapping was verified against.</summary>
    private const string TestedCodexVersionPrefix = "0.159.";

    private enum Level
    {
        Ok,
        Warn,
        Fail,
        Skip,
    }

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        void Report(Level level, string check, string detail = "")
        {
            if (level == Level.Fail)
            {
                failures++;
            }

            var tag = level switch
            {
                Level.Ok => "[ ok ]",
                Level.Warn => "[warn]",
                Level.Fail => "[FAIL]",
                _ => "[skip]",
            };
            Console.WriteLine(detail.Length == 0 ? $"{tag} {check}" : $"{tag} {check}: {detail}");
        }

        // Local setup.
        var paths = AppPaths.Default();
        paths.EnsureRoot();
        Report(Level.Ok, "Data directory", paths.Root);
        Report(
            FilePermissions.IsRestricted(paths.Root, isDirectory: true) ? Level.Ok : Level.Warn,
            "Data directory permissions",
            FilePermissions.IsRestricted(paths.Root, isDirectory: true) ? "current user only" : "accessible by other users");

        var configStore = new TomlConfigStore(paths.ConfigFile);
        ConfigLoadResult? config = null;
        try
        {
            config = configStore.Load();
            Report(config.Warnings.Count == 0 ? Level.Ok : Level.Warn, "Config",
                config.FileExists ? string.Join("; ", config.Warnings.DefaultIfEmpty(paths.ConfigFile)) : "no config.toml, using defaults (`resetme config --init`)");
        }
        catch (Exception ex) when (ex is IOException or Tomlyn.TomlException or UnauthorizedAccessException)
        {
            Report(Level.Fail, "Config", ex.Message);
        }

        try
        {
            var state = new JsonResetStateStore(paths.StateFile).Load();
            Report(state.Pending is null ? Level.Ok : Level.Warn, "State",
                state.Pending is null ? "no unresolved reset attempt" : "unresolved reset attempt; run `resetme reset`");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Report(Level.Fail, "State", ex.Message);
        }

        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            var probe = Path.Combine(paths.LogDirectory, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            Report(Level.Ok, "Log directory", $"{paths.LogDirectory} (level {config?.Options.Logging.Level.ToString().ToLowerInvariant() ?? "information"})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(Level.Warn, "Log directory", $"not writable: {ex.Message}");
        }

        using (var handle = new FileResetLock(paths.LockFile).TryAcquire())
        {
            Report(handle is null ? Level.Warn : Level.Ok, "Reset lock",
                handle is null ? "held by another ResetMe process" : "free");
        }

        // Codex.
        var configured = config?.Options.CodexExecutable;
        var executable = CodexLocator.Resolve(configured);
        if (executable is null)
        {
            Report(Level.Fail, "Codex executable", "not found (install Codex or set [codex] executable)");
            return Finish(failures);
        }

        Report(Level.Ok, "Codex executable", executable);

        CodexAppServerClient client;
        try
        {
            client = await CodexAppServerClient.StartAsync(
                new CodexClientOptions { Executable = configured },
                TimeProvider.System,
                cancellationToken).ConfigureAwait(false);
        }
        catch (CodexUnavailableException ex)
        {
            Report(Level.Fail, "Codex App Server", ex.Message);
            return Finish(failures);
        }

        await using (client)
        {
            var version = client.CodexVersion ?? "unknown";
            Report(Level.Ok, "Codex App Server", "initialize succeeded");
            Report(version.StartsWith(TestedCodexVersionPrefix, StringComparison.Ordinal) ? Level.Ok : Level.Warn,
                "Codex version", version.StartsWith(TestedCodexVersionPrefix, StringComparison.Ordinal)
                    ? version
                    : $"{version} (verified against {TestedCodexVersionPrefix}x; protocol may differ)");

            try
            {
                var account = await client.GetAccountAsync(cancellationToken).ConfigureAwait(false);
                if (!account.IsAuthenticated)
                {
                    Report(Level.Fail, "Authentication", "not logged in (`codex login`)");
                    return Finish(failures);
                }

                Report(Level.Ok, "Authentication", $"{account.AccountType ?? "unknown"} / plan {account.PlanType ?? "unknown"}");

                var usage = await client.GetUsageAsync(includeCreditDetails: false, cancellationToken).ConfigureAwait(false);
                var windows = usage.FiveHour is not null && usage.Weekly is not null;
                Report(windows ? Level.Ok : Level.Warn, "Rate limits",
                    windows ? "5-hour and weekly windows readable" : "one or both windows missing from the response");
                Report(usage.UsageAllowed is null ? Level.Warn : Level.Ok, "Usage permission flag",
                    usage.UsageAllowed is null ? "not reported; detection falls back to percentages" : "reported");
                Report(usage.ResetCreditsReported ? Level.Ok : Level.Fail, "Reset capability",
                    usage.ResetCreditsReported ? $"{usage.AvailableResetCount} credit(s) available" : "backend sent no reset-credit data");
            }
            catch (Exception ex) when (ex is CodexTransientException or Codex.JsonRpc.JsonRpcException or CodexProtocolException)
            {
                Report(Level.Fail, "Codex request", ex.Message);
            }
        }

        Report(Level.Skip, "Desktop notifications", "arrives in MVP-2; terminal output is used");
        return Finish(failures);
    }

    private static int Finish(int failures)
    {
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
        return failures == 0 ? ExitCodes.Ok : ExitCodes.Error;
    }
}
