namespace Fermata.Platform;

/// <summary>Per-OS data locations (PRD §22). The only place that knows platform paths.</summary>
public sealed class AppPaths
{
    public const string HomeVariable = "FERMATA_HOME";

    /// <summary>Pre-rename override, still honoured so existing setups keep their data.</summary>
    public const string LegacyHomeVariable = "RESETME_HOME";

    public AppPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string ConfigFile => Path.Combine(Root, "config.toml");

    public string StateFile => Path.Combine(Root, "state.json");

    public string LockFile => Path.Combine(Root, "state.lock");

    /// <summary>Last monitor view for hooks and the MCP server (see StatusSnapshot).</summary>
    public string StatusFile => Path.Combine(Root, "status.json");

    /// <summary>Last Claude Code quota seen by the status line wrapper.</summary>
    public string ClaudeQuotaFile => Path.Combine(Root, "claude-quota.json");

    /// <summary>Claude Code sessions seen by the hooks (alive or ended).</summary>
    public string ClaudeSessionsDirectory => Path.Combine(Root, "claude-sessions");

    public string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>One folder per supervised job (job.json, events.ndjson, checkpoints/).</summary>
    public string JobsDirectory => Path.Combine(Root, "jobs");

    /// <summary>Held by the one process that runs the job scheduler.</summary>
    public string JobsLockFile => Path.Combine(Root, "jobs.lock");

    /// <summary>Written once the ResetMe data was copied over (or there was none to copy).</summary>
    public string MigrationMarker => Path.Combine(Root, ".migrated-from-resetme");

    /// <summary>
    /// Default root; <c>FERMATA_HOME</c> (or the older <c>RESETME_HOME</c>) overrides it (tests,
    /// portable installs).
    /// </summary>
    public static AppPaths Default()
    {
        foreach (var variable in new[] { HomeVariable, LegacyHomeVariable })
        {
            var overridden = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(overridden))
            {
                return new AppPaths(Path.GetFullPath(overridden));
            }
        }

        return new AppPaths(Path.Combine(ConfigBase(), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "Fermata" : "fermata"));
    }

    /// <summary>
    /// Where ResetMe (before the rename) kept its data; null when an override variable is set,
    /// because then the user chose the location explicitly.
    /// </summary>
    public static string? LegacyRoot()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HomeVariable))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LegacyHomeVariable)))
        {
            return null;
        }

        return Path.Combine(ConfigBase(), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "ResetMe" : "resetme");
    }

    private static string ConfigBase()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".config") : xdg;
    }

    public void EnsureRoot()
    {
        if (!Directory.Exists(Root))
        {
            Directory.CreateDirectory(Root);
            FilePermissions.RestrictToCurrentUser(Root, isDirectory: true);
        }
    }
}
