using System.Security;
using System.Text;
using ResetMe.Core.Ports;
using ResetMe.Platform.Processes;

namespace ResetMe.Platform.Notifications;

public static class NotifierFactory
{
    /// <summary>The native notifier for this OS, or a no-op one when disabled/unsupported.</summary>
    public static INotifier Create(bool enabled, IProcessRunner? runner = null)
    {
        runner ??= ProcessRunner.Instance;
        if (!enabled)
        {
            return new NullNotifier("disabled in config");
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsToastNotifier(runner);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsNotifier(runner);
        }

        return new LinuxNotifier(runner);
    }
}

public sealed class NullNotifier(string reason) : INotifier
{
    public string Mechanism => $"none ({reason})";

    public bool IsAvailable => false;

    public Task<bool> ShowAsync(Notification notification, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>Base for notifiers that shell out to a helper; swallows every failure (FR-07 fallback).</summary>
public abstract class ProcessNotifier(IProcessRunner runner) : INotifier
{
    protected static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    protected IProcessRunner Runner { get; } = runner;

    public abstract string Mechanism { get; }

    public abstract bool IsAvailable { get; }

    public async Task<bool> ShowAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!IsAvailable)
        {
            return false;
        }

        try
        {
            var (file, args, env) = BuildCommand(notification);
            var result = await Runner.RunAsync(file, args, Timeout, cancellationToken, env).ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    internal abstract (string File, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment) BuildCommand(Notification notification);
}

/// <summary>
/// Windows toast through Windows PowerShell's WinRT bridge: no extra packages and no Windows-only
/// target framework. Content travels in environment variables, never on the command line.
/// </summary>
public sealed class WindowsToastNotifier(IProcessRunner runner) : ProcessNotifier(runner)
{
    // AppUserModelID of Windows PowerShell; always registered, so the toast is shown without installing a shortcut.
    internal const string AppId = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";

    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
        $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
        $xml.LoadXml($env:RESETME_TOAST_XML)
        $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:RESETME_TOAST_APPID).Show($toast)
        """;

    public override string Mechanism => "Windows toast (PowerShell WinRT)";

    public override bool IsAvailable => File.Exists(PowerShellPath);

    private static string PowerShellPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    internal static string ToastXml(Notification notification) =>
        "<toast><visual><binding template=\"ToastGeneric\">"
        + $"<text>{SecurityElement.Escape(notification.Title)}</text>"
        + $"<text>{SecurityElement.Escape(notification.Body)}</text>"
        + "</binding></visual></toast>";

    internal override (string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?) BuildCommand(Notification notification)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
        return (
            PowerShellPath,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            new Dictionary<string, string>
            {
                ["RESETME_TOAST_XML"] = ToastXml(notification),
                ["RESETME_TOAST_APPID"] = AppId,
            });
    }
}

/// <summary>macOS Notification Center via osascript; text is passed as argv, so no AppleScript escaping.</summary>
public sealed class MacOsNotifier(IProcessRunner runner) : ProcessNotifier(runner)
{
    private const string Osascript = "/usr/bin/osascript";

    public override string Mechanism => "osascript display notification";

    public override bool IsAvailable => File.Exists(Osascript);

    internal override (string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?) BuildCommand(Notification notification) =>
        (Osascript,
        [
            "-e", "on run argv",
            "-e", "display notification (item 2 of argv) with title (item 1 of argv)",
            "-e", "end run",
            notification.Title,
            notification.Body,
        ],
        null);
}

/// <summary>freedesktop notifications via notify-send; requires a graphical session.</summary>
public sealed class LinuxNotifier(IProcessRunner runner) : ProcessNotifier(runner)
{
    private readonly string? _notifySend = runner.FindOnPath("notify-send");

    public override string Mechanism => _notifySend is null ? "none (notify-send not installed)" : "notify-send";

    public override bool IsAvailable =>
        _notifySend is not null
        && (HasEnv("DISPLAY") || HasEnv("WAYLAND_DISPLAY") || HasEnv("DBUS_SESSION_BUS_ADDRESS"));

    internal override (string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?) BuildCommand(Notification notification) =>
        (_notifySend!,
        [
            "--app-name=ResetMe",
            $"--urgency={(notification.Kind == NotificationKind.LimitReached ? "critical" : "normal")}",
            "--",
            notification.Title,
            notification.Body,
        ],
        null);

    private static bool HasEnv(string name) => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));
}
