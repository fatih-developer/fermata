using System.Diagnostics;
using Fermata.Platform.Processes;
using Microsoft.Win32;

namespace Fermata.Platform;

public enum MigrationOutcome
{
    /// <summary>Nothing to do: already migrated, a fresh install, or the user chose a data root.</summary>
    NotNeeded,
    Migrated,

    /// <summary>ResetMe still runs. Two apps with two lock files could redeem two credits for one limit.</summary>
    BlockedByRunningResetMe,
}

public sealed record MigrationResult(MigrationOutcome Outcome, IReadOnlyList<string> CopiedFiles, bool LegacyAutostartWasEnabled)
{
    public static readonly MigrationResult NotNeeded = new(MigrationOutcome.NotNeeded, [], false);

    public const string BlockedMessage =
        "ResetMe (the previous name of Fermata) is still running. Quit it first (tray icon → Quit; "
        + "for a background daemon run `resetme autostart disable --target daemon`), then start Fermata again. "
        + "Running both at once could use two reset credits for one limit.";
}

/// <summary>
/// First start after the rename: copies ResetMe's config.toml and state.json (with its backup, which
/// may hold a pending idempotency key) into the Fermata data root, and removes ResetMe's login
/// entry. Refuses while ResetMe still runs. Never deletes the old folder.
/// </summary>
public sealed class LegacyMigration
{
    private static readonly string[] Files = ["config.toml", "state.json", "state.json.bak"];

    private readonly AppPaths _paths;
    private readonly string? _legacyRoot;
    private readonly Func<string, bool> _legacyRunning;
    private readonly Func<CancellationToken, Task<bool>> _removeLegacyAutostart;

    public LegacyMigration(
        AppPaths paths,
        string? legacyRoot,
        Func<string, bool>? legacyRunning = null,
        Func<CancellationToken, Task<bool>>? removeLegacyAutostart = null)
    {
        _paths = paths;
        _legacyRoot = legacyRoot;
        _legacyRunning = legacyRunning ?? IsResetMeRunning;
        _removeLegacyAutostart = removeLegacyAutostart ?? (ct => LegacyAutostart.RemoveAsync(ProcessRunner.Instance, ct));
    }

    public static LegacyMigration ForDefaultPaths(AppPaths paths) => new(paths, AppPaths.LegacyRoot());

    public bool IsNeeded =>
        _legacyRoot is not null
        && Directory.Exists(_legacyRoot)
        && !File.Exists(_paths.MigrationMarker)
        && !File.Exists(_paths.StateFile)
        && !File.Exists(_paths.ConfigFile);

    public async Task<MigrationResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!IsNeeded)
        {
            return MigrationResult.NotNeeded;
        }

        var legacy = _legacyRoot!;
        if (_legacyRunning(legacy))
        {
            return new MigrationResult(MigrationOutcome.BlockedByRunningResetMe, [], false);
        }

        _paths.EnsureRoot();
        var copied = new List<string>();
        foreach (var name in Files)
        {
            var source = Path.Combine(legacy, name);
            var target = Path.Combine(_paths.Root, name);
            if (File.Exists(source) && !File.Exists(target))
            {
                File.Copy(source, target);
                FilePermissions.RestrictToCurrentUser(target, isDirectory: false);
                copied.Add(name);
            }
        }

        bool autostart;
        try
        {
            autostart = await _removeLegacyAutostart(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            autostart = false; // A leftover login entry only starts the old app; it is not worth failing over.
        }

        // Last, so an interrupted copy is retried on the next start.
        await File.WriteAllTextAsync(_paths.MigrationMarker, $"Copied from {legacy}: {string.Join(", ", copied)}{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
        return new MigrationResult(MigrationOutcome.Migrated, copied, autostart);
    }

    /// <summary>The ResetMe tray app holds desktop.lock while it runs; the CLI daemon/watch show up as processes.</summary>
    internal static bool IsResetMeRunning(string legacyRoot)
    {
        var lockFile = Path.Combine(legacyRoot, "desktop.lock");
        if (File.Exists(lockFile))
        {
            try
            {
                using var probe = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        // Only processes that monitor and may reset count. `resetme mcp` and `resetme hook`, started by
        // open Codex sessions, are read-only and must not hold up the move.
        var processes = Process.GetProcessesByName("ResetMeApp").Concat(Process.GetProcessesByName("resetme")).ToList();
        try
        {
            return processes.Any(p => p.Id != Environment.ProcessId && (p.ProcessName == "ResetMeApp" || IsMonitoringCli(p.Id)));
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>`resetme daemon` or `resetme watch`. The command line is only readable on Linux; elsewhere the user is told to stop it.</summary>
    private static bool IsMonitoringCli(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            var args = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            return args.Skip(1).Any(a => a is "daemon" or "watch");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>ResetMe's start-at-login entries (pre-rename names).</summary>
public static class LegacyAutostart
{
    /// <summary>Removes them; true when the tray app was set to start at login.</summary>
    public static async Task<bool> RemoveAsync(IProcessRunner runner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(Autostart.WindowsRunKeyAutostart.RunKeyPath, writable: true);
            var enabled = key?.GetValue("ResetMe") is not null;
            key?.DeleteValue("ResetMe", throwOnMissingValue: false);
            return enabled;
        }

        if (OperatingSystem.IsMacOS())
        {
            var agents = Path.Combine(home, "Library", "LaunchAgents");
            var desktop = Path.Combine(agents, "com.resetme.desktop.plist");
            var daemon = Path.Combine(agents, "com.resetme.daemon.plist");
            var enabled = File.Exists(desktop);
            File.Delete(desktop);
            File.Delete(daemon);
            return enabled;
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var config = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".config") : xdg;
        var entry = Path.Combine(config, "autostart", "resetme.desktop");
        var service = Path.Combine(config, "systemd", "user", "resetme.service");
        var wasEnabled = File.Exists(entry);
        File.Delete(entry);
        if (File.Exists(service))
        {
            File.Delete(service);
            if (runner.FindOnPath("systemctl") is { } systemctl)
            {
                await runner.RunAsync(systemctl, ["--user", "daemon-reload"], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }
        }

        return wasEnabled;
    }
}
