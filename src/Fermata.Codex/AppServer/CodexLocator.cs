namespace Fermata.Codex.AppServer;

/// <summary>Finds the codex executable: explicit config path first, then PATH.</summary>
public static class CodexLocator
{
    public static string? Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { "codex.exe", "codex.cmd", "codex.bat" }
            : new[] { "codex" };

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>npm-style shims (.cmd/.bat) must be launched through cmd.exe.</summary>
    public static bool NeedsShell(string executable) =>
        OperatingSystem.IsWindows()
        && (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
}
