using Microsoft.Extensions.Logging;
using Fermata.Core.Domain;
using Fermata.Core.Monitoring;
using Fermata.Core.Policies;
using Fermata.Core.Ports;
using Fermata.Core.Reset;
using Fermata.Platform;
using Fermata.Platform.Autostart;
using Fermata.Platform.Notifications;
using Fermata.Platform.Processes;

namespace Fermata.Core.Tests;

/// <summary>MVP-2 platform pieces: autostart, notifications, settings persistence.</summary>
public sealed class DesktopIntegrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fermata-mvp2-").FullName;
    private readonly RecordingRunner _runner = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AutostartEntry Desktop(string path = "/Applications/Fermata.app/Contents/MacOS/FermataApp") =>
        new(AutostartTarget.Desktop, path, ["--minimized"]);

    private static AutostartEntry Daemon(string path = "/opt/reset me/fermata") =>
        new(AutostartTarget.Daemon, path, ["daemon"]);

    [Fact]
    public void Launch_agent_plist_is_valid_and_escaped()
    {
        var plist = MacLaunchAgentAutostart.Plist(Daemon("/Users/a&b/fermata"), "/Users/a&b/logs");

        // Well-formed XML (the DOCTYPE is ignored, never fetched).
        using var reader = System.Xml.XmlReader.Create(new StringReader(plist), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore });
        Assert.NotNull(System.Xml.Linq.XDocument.Load(reader).Root);
        Assert.Contains("<string>com.fermata.daemon</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>/Users/a&amp;b/fermata</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>daemon</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>KeepAlive</key>", plist, StringComparison.Ordinal);
        Assert.DoesNotContain("<key>KeepAlive</key>", MacLaunchAgentAutostart.Plist(Desktop(), "/tmp"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mac_daemon_is_written_and_bootstrapped()
    {
        _runner.Results["/usr/bin/id"] = new ProcessResult(0, "501\n", "");
        var manager = new MacLaunchAgentAutostart(Path.Combine(_dir, "LaunchAgents"), "/tmp/logs", _runner);

        await manager.EnableAsync(Daemon(), CancellationToken.None);

        Assert.True(manager.GetStatus(AutostartTarget.Daemon).Enabled);
        Assert.Contains(_runner.Calls, c => c == "/bin/launchctl bootstrap gui/501 " + Path.Combine(_dir, "LaunchAgents", "com.fermata.daemon.plist"));

        await manager.DisableAsync(AutostartTarget.Daemon, CancellationToken.None);
        Assert.False(manager.GetStatus(AutostartTarget.Daemon).Enabled);
        Assert.Contains(_runner.Calls, c => c == "/bin/launchctl bootout gui/501/com.fermata.daemon");
    }

    [Fact]
    public async Task Mac_desktop_entry_is_only_written_not_started()
    {
        var manager = new MacLaunchAgentAutostart(Path.Combine(_dir, "LaunchAgents"), "/tmp/logs", _runner);

        await manager.EnableAsync(Desktop(), CancellationToken.None);

        Assert.True(manager.GetStatus(AutostartTarget.Desktop).Enabled);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public void Systemd_unit_and_desktop_entry_quote_paths_with_spaces()
    {
        var unit = LinuxAutostart.ServiceUnit(Daemon());
        Assert.Contains("ExecStart=\"/opt/reset me/fermata\" daemon", unit, StringComparison.Ordinal);
        Assert.Contains("Restart=on-failure", unit, StringComparison.Ordinal);
        Assert.Contains("WantedBy=default.target", unit, StringComparison.Ordinal);

        var desktop = LinuxAutostart.DesktopFile(Desktop("/opt/fermata/FermataApp"));
        Assert.Contains("Exec=/opt/fermata/FermataApp --minimized", desktop, StringComparison.Ordinal);
        Assert.Contains("X-GNOME-Autostart-enabled=true", desktop, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linux_daemon_uses_systemctl_user()
    {
        _runner.OnPath["systemctl"] = "/usr/bin/systemctl";
        var manager = new LinuxAutostart(_dir, _runner);

        await manager.EnableAsync(Daemon(), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_dir, "systemd", "user", "fermata.service")));
        Assert.Equal(
            ["/usr/bin/systemctl --user daemon-reload", "/usr/bin/systemctl --user enable --now fermata.service"],
            _runner.Calls);

        await manager.DisableAsync(AutostartTarget.Daemon, CancellationToken.None);
        Assert.False(manager.GetStatus(AutostartTarget.Daemon).Enabled);
        Assert.Contains("/usr/bin/systemctl --user disable --now fermata.service", _runner.Calls);
    }

    [Fact]
    public async Task Linux_desktop_autostart_is_an_xdg_file()
    {
        var manager = new LinuxAutostart(_dir, _runner);

        await manager.EnableAsync(Desktop("/opt/fermata/FermataApp"), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_dir, "autostart", "fermata.desktop")));
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public void Windows_command_line_quotes_paths()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(
            "\"C:\\Program Files\\Fermata\\FermataApp.exe\" --minimized",
            WindowsRunKeyAutostart.CommandLine(new AutostartEntry(AutostartTarget.Desktop, @"C:\Program Files\Fermata\FermataApp.exe", ["--minimized"])));
    }

    [Fact]
    public async Task Windows_run_key_round_trip_on_a_test_key()
    {
        if (OperatingSystem.IsWindows())
        {
            await RunKeyRoundTrip();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task RunKeyRoundTrip()
    {
        // A private test key, never the real Run key.
        var keyPath = $@"Software\Fermata-Tests\{Guid.NewGuid():N}";
        var manager = new WindowsRunKeyAutostart(keyPath);
        try
        {
            await manager.EnableAsync(new AutostartEntry(AutostartTarget.Desktop, @"C:\x\FermataApp.exe", ["--minimized"]), CancellationToken.None);
            Assert.Equal(@"C:\x\FermataApp.exe --minimized", manager.GetStatus(AutostartTarget.Desktop).Command);

            await Assert.ThrowsAsync<PlatformNotSupportedException>(() => manager.EnableAsync(Daemon(), CancellationToken.None));

            await manager.DisableAsync(AutostartTarget.Desktop, CancellationToken.None);
            Assert.False(manager.GetStatus(AutostartTarget.Desktop).Enabled);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Fermata-Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Toast_xml_escapes_text()
    {
        var xml = WindowsToastNotifier.ToastXml(new Notification(NotificationKind.Info, "A & B", "<1m left"));

        Assert.Contains("<text>A &amp; B</text>", xml, StringComparison.Ordinal);
        Assert.Contains("<text>&lt;1m left</text>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Toast_content_travels_in_environment_not_arguments()
    {
        var (_, args, env) = new WindowsToastNotifier(_runner).BuildCommand(new Notification(NotificationKind.Info, "Title", "secret-ish body"));

        Assert.DoesNotContain(args, a => a.Contains("secret-ish", StringComparison.Ordinal));
        Assert.Contains("secret-ish body", env!["FERMATA_TOAST_XML"], StringComparison.Ordinal);
    }

    [Fact]
    public void Mac_notification_passes_text_as_argv()
    {
        var (file, args, _) = new MacOsNotifier(_runner).BuildCommand(new Notification(NotificationKind.Info, "Say \"hi\"", "body"));

        Assert.Equal("/usr/bin/osascript", file);
        Assert.Equal("Say \"hi\"", args[^2]);
        Assert.Equal("body", args[^1]);
    }

    [Fact]
    public void Linux_limit_notification_is_critical()
    {
        _runner.OnPath["notify-send"] = "/usr/bin/notify-send";
        var (_, args, _) = new LinuxNotifier(_runner).BuildCommand(new Notification(NotificationKind.LimitReached, "t", "b"));

        Assert.Contains("--urgency=critical", args);
        Assert.Equal(["--", "t", "b"], args.TakeLast(3));
    }

    [Fact]
    public async Task Notifier_failures_never_throw()
    {
        _runner.Throw = true;
        var notifier = new MacOsNotifierForTests(_runner);

        Assert.False(await notifier.ShowAsync(new Notification(NotificationKind.Info, "t", "b"), CancellationToken.None));
    }

    [Fact]
    public void Disabled_notifications_use_the_null_notifier()
    {
        var notifier = NotifierFactory.Create(enabled: false);

        Assert.False(notifier.IsAvailable);
        Assert.StartsWith("none", notifier.Mechanism, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notifying_observer_sends_one_notice_per_limit_and_per_result()
    {
        var inner = new SilentObserver();
        var notifier = new CollectingNotifier();
        var observer = new NotifyingObserver(inner, notifier, new SteppingTimeProvider(Usage.Now), NotificationTexts.TerminalAskHint);
        var usage = Usage.Blocked();
        var assessment = LimitEvaluator.Assess(usage, new GuardOptions(), Usage.Now);

        observer.OnLimitReached(new LimitNotice(usage, assessment, LimitHandling.AskUser));
        await observer.LastNotification;
        observer.OnResetCompleted(new ResetReport(ResetRunStatus.Succeeded, ResetOutcome.Reset, UsageAfter: Usage.Healthy(credits: 1)));
        await observer.LastNotification;
        observer.OnResetCompleted(new ResetReport(ResetRunStatus.LockBusy));

        Assert.Equal(["5-hour Codex limit reached", "Codex reset applied"], notifier.Titles);
        Assert.EndsWith(NotificationTexts.TerminalAskHint, notifier.Bodies[0], StringComparison.Ordinal);
        Assert.Equal(1, inner.Limits);
        Assert.Equal(2, inner.Reports);
        Assert.False(observer.CanConfirm);
    }

    [Fact]
    public void Settings_survive_a_save_and_load_round_trip()
    {
        var path = Path.Combine(_dir, "config.toml");
        var options = new GuardOptions { Mode = GuardMode.Manual, NotificationsEnabled = false, StartMinimized = false, CodexExecutable = @"C:\tools\codex ""x"".exe" };
        options.Monitor.IntervalSeconds = 45;
        options.Limits.Weekly = false;
        options.Reset.MinTimeToNaturalResetMinutes = 30;
        options.Automatic.MaxResetsPerWeek = 5;
        options.Logging.Level = LogLevel.Debug;

        var store = new TomlConfigStore(path);
        store.Save(options);
        var loaded = store.Load();

        Assert.Empty(loaded.Warnings);
        Assert.Equal(GuardMode.Manual, loaded.Options.Mode);
        Assert.Equal(45, loaded.Options.Monitor.IntervalSeconds);
        Assert.False(loaded.Options.Limits.Weekly);
        Assert.Equal(30, loaded.Options.Reset.MinTimeToNaturalResetMinutes);
        Assert.Equal(5, loaded.Options.Automatic.MaxResetsPerWeek);
        Assert.Equal(LogLevel.Debug, loaded.Options.Logging.Level);
        Assert.False(loaded.Options.NotificationsEnabled);
        Assert.False(loaded.Options.StartMinimized);
        Assert.Equal(options.CodexExecutable, loaded.Options.CodexExecutable);
        Assert.Equal(["config.toml"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public Dictionary<string, ProcessResult> Results { get; } = [];

        public Dictionary<string, string> OnPath { get; } = [];

        public bool Throw { get; set; }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
        {
            if (Throw)
            {
                throw new TimeoutException("helper hung");
            }

            if (fileName != "/usr/bin/id")
            {
                Calls.Add(string.Join(' ', new[] { fileName }.Concat(arguments)));
            }

            return Task.FromResult(Results.TryGetValue(fileName, out var result) ? result : new ProcessResult(0, "", ""));
        }

        public string? FindOnPath(string name) => OnPath.GetValueOrDefault(name);
    }

    /// <summary>MacOsNotifier reports unavailable off macOS; this variant is always available.</summary>
    private sealed class MacOsNotifierForTests(IProcessRunner runner) : ProcessNotifier(runner)
    {
        public override string Mechanism => "test";

        public override bool IsAvailable => true;

        internal override (string File, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment) BuildCommand(Notification notification) =>
            ("/usr/bin/true", [], null);
    }

    private sealed class CollectingNotifier : INotifier
    {
        public List<string> Titles { get; } = [];

        public List<string> Bodies { get; } = [];

        public string Mechanism => "collect";

        public bool IsAvailable => true;

        public Task<bool> ShowAsync(Notification notification, CancellationToken cancellationToken)
        {
            Titles.Add(notification.Title);
            Bodies.Add(notification.Body);
            return Task.FromResult(true);
        }
    }

    private sealed class SilentObserver : IMonitorObserver
    {
        public int Limits { get; private set; }

        public int Reports { get; private set; }

        public bool CanConfirm => false;

        public void OnUsage(CodexUsage usage, LimitAssessment assessment)
        {
        }

        public void OnLimitReached(LimitNotice notice) => Limits++;

        public Task<bool> ConfirmResetAsync(LimitNotice notice, CancellationToken cancellationToken) => Task.FromResult(false);

        public void OnResetCompleted(ResetReport report) => Reports++;

        public void OnResetFailed(Exception error)
        {
        }

        public void OnAuthRequired()
        {
        }

        public void OnUnavailable(Exception error, TimeSpan retryIn)
        {
        }
    }
}
