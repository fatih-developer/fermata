namespace CodexResetGuard.Platform;

/// <summary>Per-OS data locations (PRD §22). The only place that knows platform paths.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string ConfigFile => Path.Combine(Root, "config.toml");

    public string StateFile => Path.Combine(Root, "state.json");

    public string LockFile => Path.Combine(Root, "state.lock");

    public string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>
    /// Default root; <c>CODEX_RESET_GUARD_HOME</c> overrides it (tests, portable installs).
    /// </summary>
    public static AppPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable("CODEX_RESET_GUARD_HOME");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AppPaths(Path.GetFullPath(overridden));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new AppPaths(Path.Combine(appData, "CodexResetGuard"));
        }

        if (OperatingSystem.IsMacOS())
        {
            return new AppPaths(Path.Combine(home, "Library", "Application Support", "CodexResetGuard"));
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".config") : xdg;
        return new AppPaths(Path.Combine(configHome, "codex-reset-guard"));
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
