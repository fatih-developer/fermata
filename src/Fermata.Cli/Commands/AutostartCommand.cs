using Fermata.Platform.Autostart;

namespace Fermata.Cli.Commands;

/// <summary>`fermata autostart status|enable|disable [--target desktop|daemon] [--path exe]`.</summary>
internal static class AutostartCommand
{
    public const string DesktopExecutableName = "FermataApp";

    public static async Task<int> RunAsync(string action, string? target, string? path, CancellationToken cancellationToken)
    {
        var manager = AutostartFactory.Create();
        AutostartTarget resolved;
        try
        {
            resolved = ResolveTarget(target);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Error;
        }

        switch (action)
        {
            case "status":
                foreach (var t in new[] { AutostartTarget.Desktop, AutostartTarget.Daemon })
                {
                    var status = manager.GetStatus(t);
                    Console.WriteLine($"{t,-8} {(status.Enabled ? "enabled " : "disabled")} {status.Mechanism}: {status.Location}");
                }

                return ExitCodes.Ok;

            case "enable":
                var entry = BuildEntry(resolved, path);
                if (entry is null)
                {
                    return ExitCodes.Error;
                }

                try
                {
                    await manager.EnableAsync(entry, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Could not enable autostart: {ex.Message}");
                    return ExitCodes.Error;
                }

                var enabled = manager.GetStatus(resolved);
                Console.WriteLine($"Autostart enabled for {resolved.ToString().ToLowerInvariant()} ({enabled.Mechanism}).");
                Console.WriteLine($"  {enabled.Location}");
                Console.WriteLine($"  starts: {entry.ExecutablePath} {string.Join(' ', entry.Arguments)}");
                return ExitCodes.Ok;

            case "disable":
                try
                {
                    await manager.DisableAsync(resolved, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Could not disable autostart: {ex.Message}");
                    return ExitCodes.Error;
                }

                Console.WriteLine($"Autostart disabled for {resolved.ToString().ToLowerInvariant()}.");
                return ExitCodes.Ok;

            default:
                Console.Error.WriteLine("Use: fermata autostart status|enable|disable");
                return ExitCodes.Error;
        }
    }

    /// <summary>Default: the tray app on desktops, the daemon on headless Linux.</summary>
    internal static AutostartTarget ResolveTarget(string? target) => target?.ToLowerInvariant() switch
    {
        "desktop" => AutostartTarget.Desktop,
        "daemon" => AutostartTarget.Daemon,
        null or "" => OperatingSystem.IsLinux() && !HasGraphicalSession() ? AutostartTarget.Daemon : AutostartTarget.Desktop,
        _ => throw new ArgumentException($"Unknown target '{target}'; use desktop or daemon."),
    };

    private static AutostartEntry? BuildEntry(AutostartTarget target, string? path)
    {
        if (target == AutostartTarget.Daemon)
        {
            var self = path ?? Environment.ProcessPath;
            if (self is null)
            {
                Console.Error.WriteLine("Cannot determine the fermata executable path; pass --path.");
                return null;
            }

            return new AutostartEntry(target, Path.GetFullPath(self), ["daemon"]);
        }

        var desktop = path ?? FindDesktopExecutable();
        if (desktop is null || !File.Exists(desktop))
        {
            Console.Error.WriteLine($"Fermata desktop app not found next to fermata ({AppContext.BaseDirectory}). Pass --path to it.");
            return null;
        }

        return new AutostartEntry(target, Path.GetFullPath(desktop), ["--minimized"]);
    }

    private static string? FindDesktopExecutable()
    {
        var name = OperatingSystem.IsWindows() ? DesktopExecutableName + ".exe" : DesktopExecutableName;
        var sibling = Path.Combine(AppContext.BaseDirectory, name);
        // Packages (and the macOS .app bundle's Contents/MacOS) ship both executables side by side.
        return File.Exists(sibling) ? sibling : null;
    }

    private static bool HasGraphicalSession() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
}
