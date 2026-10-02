using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;
using ResetMe.Platform.Processes;

namespace ResetMe.Platform.Autostart;

/// <summary>What starts at login: the tray app, or the headless monitor (`resetme daemon`).</summary>
public enum AutostartTarget
{
    Desktop,
    Daemon,
}

public sealed record AutostartEntry(AutostartTarget Target, string ExecutablePath, IReadOnlyList<string> Arguments);

public sealed record AutostartStatus(bool Enabled, string Mechanism, string Location, string? Command);

/// <summary>Start-at-login registration (PRD §23, MVP-2).</summary>
public interface IAutostartManager
{
    AutostartStatus GetStatus(AutostartTarget target);

    Task EnableAsync(AutostartEntry entry, CancellationToken cancellationToken);

    Task DisableAsync(AutostartTarget target, CancellationToken cancellationToken);
}

public static class AutostartFactory
{
    public static IAutostartManager Create(IProcessRunner? runner = null)
    {
        runner ??= ProcessRunner.Instance;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            return new WindowsRunKeyAutostart(WindowsRunKeyAutostart.RunKeyPath);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacLaunchAgentAutostart(Path.Combine(home, "Library", "LaunchAgents"), AppPaths.Default().LogDirectory, runner);
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var config = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".config") : xdg;
        return new LinuxAutostart(config, runner);
    }

    internal static string Label(AutostartTarget target) => target == AutostartTarget.Desktop ? "desktop" : "daemon";
}

/// <summary>HKCU Run key. Only the GUI app is registered: a console daemon would open a window at login.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRunKeyAutostart(string keyPath) : IAutostartManager
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ResetMe";

    public AutostartStatus GetStatus(AutostartTarget target)
    {
        if (target == AutostartTarget.Daemon)
        {
            return new AutostartStatus(false, "not used on Windows", "the tray app monitors in the background", null);
        }

        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        var command = key?.GetValue(ValueName) as string;
        return new AutostartStatus(command is not null, "Windows Run key", $@"HKCU\{keyPath}\{ValueName}", command);
    }

    public Task EnableAsync(AutostartEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Target == AutostartTarget.Daemon)
        {
            throw new PlatformNotSupportedException(
                "On Windows the tray app does the background monitoring; enable autostart for the desktop app instead.");
        }

        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        key.SetValue(ValueName, CommandLine(entry), RegistryValueKind.String);
        return Task.CompletedTask;
    }

    public Task DisableAsync(AutostartTarget target, CancellationToken cancellationToken)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        return Task.CompletedTask;
    }

    internal static string CommandLine(AutostartEntry entry) =>
        string.Join(' ', new[] { Quote(entry.ExecutablePath) }.Concat(entry.Arguments.Select(Quote)));

    private static string Quote(string value) =>
        value.Length > 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"', StringComparison.Ordinal)
            ? value
            : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

/// <summary>~/Library/LaunchAgents/com.resetme.{desktop|daemon}.plist.</summary>
public sealed class MacLaunchAgentAutostart(string agentsDirectory, string logDirectory, IProcessRunner runner) : IAutostartManager
{
    public static string LabelFor(AutostartTarget target) => $"com.resetme.{AutostartFactory.Label(target)}";

    public AutostartStatus GetStatus(AutostartTarget target)
    {
        var path = PlistPath(target);
        return new AutostartStatus(File.Exists(path), "launchd LaunchAgent", path, File.Exists(path) ? File.ReadAllText(path) : null);
    }

    public async Task EnableAsync(AutostartEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(agentsDirectory);
        var path = PlistPath(entry.Target);
        await File.WriteAllTextAsync(path, Plist(entry, logDirectory), cancellationToken).ConfigureAwait(false);

        if (entry.Target == AutostartTarget.Daemon)
        {
            // Start the background monitor now; the desktop app is simply picked up at next login.
            var domain = await GuiDomainAsync(cancellationToken).ConfigureAwait(false);
            await runner.RunAsync("/bin/launchctl", ["bootout", $"{domain}/{LabelFor(entry.Target)}"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var result = await runner.RunAsync("/bin/launchctl", ["bootstrap", domain, path], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"launchctl bootstrap failed: {result.StandardError.Trim()}");
            }
        }
    }

    public async Task DisableAsync(AutostartTarget target, CancellationToken cancellationToken)
    {
        var path = PlistPath(target);
        if (target == AutostartTarget.Daemon)
        {
            var domain = await GuiDomainAsync(cancellationToken).ConfigureAwait(false);
            await runner.RunAsync("/bin/launchctl", ["bootout", $"{domain}/{LabelFor(target)}"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }

        File.Delete(path);
    }

    internal static string Plist(AutostartEntry entry, string logDirectory)
    {
        var args = new StringBuilder();
        foreach (var arg in new[] { entry.ExecutablePath }.Concat(entry.Arguments))
        {
            args.Append(CultureInfo.InvariantCulture, $"\n        <string>{SecurityElement.Escape(arg)}</string>");
        }

        var daemon = entry.Target == AutostartTarget.Daemon;
        var log = SecurityElement.Escape(Path.Combine(logDirectory, $"launchd-{AutostartFactory.Label(entry.Target)}.log"));
        var keepAlive = daemon
            ? "\n    <key>KeepAlive</key>\n    <dict>\n        <key>SuccessfulExit</key>\n        <false/>\n    </dict>"
            : "";

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{LabelFor(entry.Target)}</string>
                <key>ProgramArguments</key>
                <array>{args}
                </array>
                <key>RunAtLoad</key>
                <true/>{keepAlive}
                <key>ProcessType</key>
                <string>{(daemon ? "Background" : "Interactive")}</string>
                <key>StandardOutPath</key>
                <string>{log}</string>
                <key>StandardErrorPath</key>
                <string>{log}</string>
            </dict>
            </plist>

            """;
    }

    private string PlistPath(AutostartTarget target) => Path.Combine(agentsDirectory, LabelFor(target) + ".plist");

    private async Task<string> GuiDomainAsync(CancellationToken cancellationToken)
    {
        var uid = await runner.RunAsync("/usr/bin/id", ["-u"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return $"gui/{uid.StandardOutput.Trim()}";
    }
}

/// <summary>XDG autostart for the tray app; a systemd user service for the headless daemon.</summary>
public sealed class LinuxAutostart(string configHome, IProcessRunner runner) : IAutostartManager
{
    public const string ServiceName = "resetme.service";

    public AutostartStatus GetStatus(AutostartTarget target)
    {
        var path = FilePath(target);
        var mechanism = target == AutostartTarget.Desktop ? "XDG autostart" : "systemd --user";
        return new AutostartStatus(File.Exists(path), mechanism, path, File.Exists(path) ? File.ReadAllText(path) : null);
    }

    public async Task EnableAsync(AutostartEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = FilePath(entry.Target);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (entry.Target == AutostartTarget.Desktop)
        {
            await File.WriteAllTextAsync(path, DesktopFile(entry), cancellationToken).ConfigureAwait(false);
            return;
        }

        await File.WriteAllTextAsync(path, ServiceUnit(entry), cancellationToken).ConfigureAwait(false);
        await SystemctlAsync(cancellationToken, "daemon-reload").ConfigureAwait(false);
        await SystemctlAsync(cancellationToken, "enable", "--now", ServiceName).ConfigureAwait(false);
    }

    public async Task DisableAsync(AutostartTarget target, CancellationToken cancellationToken)
    {
        var path = FilePath(target);
        if (target == AutostartTarget.Daemon && File.Exists(path))
        {
            await SystemctlAsync(cancellationToken, "disable", "--now", ServiceName).ConfigureAwait(false);
            File.Delete(path);
            await SystemctlAsync(cancellationToken, "daemon-reload").ConfigureAwait(false);
            return;
        }

        File.Delete(path);
    }

    internal static string DesktopFile(AutostartEntry entry) => $"""
        [Desktop Entry]
        Type=Application
        Name=ResetMe
        Comment=Codex usage limits and reset credits
        Exec={ExecLine(entry)}
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """;

    internal static string ServiceUnit(AutostartEntry entry) => $"""
        [Unit]
        Description=ResetMe - Codex usage limit monitor
        After=network-online.target

        [Service]
        Type=simple
        ExecStart={ExecLine(entry)}
        Restart=on-failure
        RestartSec=30

        [Install]
        WantedBy=default.target

        """;

    /// <summary>Quoting that both systemd ExecStart and the desktop-entry Exec key accept.</summary>
    internal static string ExecLine(AutostartEntry entry) =>
        string.Join(' ', new[] { entry.ExecutablePath }.Concat(entry.Arguments).Select(a =>
            a.Length > 0 && a.All(c => char.IsLetterOrDigit(c) || "-_./=:".Contains(c, StringComparison.Ordinal))
                ? a
                : "\"" + a.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));

    private string FilePath(AutostartTarget target) => target == AutostartTarget.Desktop
        ? Path.Combine(configHome, "autostart", "resetme.desktop")
        : Path.Combine(configHome, "systemd", "user", ServiceName);

    private async Task SystemctlAsync(CancellationToken cancellationToken, params string[] args)
    {
        var systemctl = runner.FindOnPath("systemctl")
            ?? throw new InvalidOperationException("systemctl not found; systemd user services are unavailable.");
        var result = await runner.RunAsync(systemctl, ["--user", .. args], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"systemctl --user {string.Join(' ', args)} failed: {result.StandardError.Trim()}");
        }
    }
}
