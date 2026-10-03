using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ResetMe.Codex.AppServer;
using ResetMe.Core.Domain;
using ResetMe.Core.Monitoring;
using ResetMe.Core.Policies;
using ResetMe.Core.Ports;
using ResetMe.Core.Reset;
using ResetMe.Desktop.ViewModels;
using ResetMe.Platform;
using ResetMe.Platform.Autostart;

namespace ResetMe.Desktop.Services;

/// <summary>UI services the host needs; implemented by the Avalonia app and by test doubles.</summary>
public interface IDesktopUi
{
    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    void Post(Action action);

    /// <summary>Shows the reset confirmation (PRD §38). True means "use a credit".</summary>
    Task<bool> ConfirmAsync(ConfirmViewModel content);

    /// <summary>A yes/no question. True means <paramref name="accept"/> was chosen.</summary>
    Task<bool> AskAsync(string title, string message, string accept, string cancel);
}

/// <summary>Composition and behaviour of the tray app: monitor lifecycle, manual reset, settings.</summary>
public sealed partial class DesktopHost : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly TomlConfigStore _configStore;
    private readonly JsonResetStateStore _stateStore;
    private readonly FileResetLock _resetLock;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly IAutostartManager _autostart;
    private readonly ICodexConnector _connector;
    private readonly Func<GuardOptions, INotifier> _notifierFactory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly IUpdateService? _updates;
    private readonly CancellationTokenSource _updateLoopStop = new();
    private string? _notifiedUpdateVersion;
    private Platform.Updates.UpdateCheckResult? _availableUpdate;

    private IDesktopUi? _ui;
    private CancellationTokenSource? _monitorStop;
    private Task _monitorTask = Task.CompletedTask;
    private INotifier _notifier;

    public DesktopHost(
        AppPaths paths,
        ICodexConnector connector,
        IAutostartManager autostart,
        Func<GuardOptions, INotifier> notifierFactory,
        ILoggerFactory loggers,
        TimeProvider time,
        IUpdateService? updates = null)
    {
        _paths = paths;
        _updates = updates;
        _configStore = new TomlConfigStore(paths.ConfigFile);
        _stateStore = new JsonResetStateStore(paths.StateFile);
        _resetLock = new FileResetLock(paths.LockFile);
        _connector = connector;
        _autostart = autostart;
        _notifierFactory = notifierFactory;
        _loggers = loggers;
        _logger = loggers.CreateLogger("ResetMe.Desktop");
        _time = time;

        var config = _configStore.Load();
        Options = config.Options;
        _notifier = notifierFactory(Options);

        ViewModel = new MainViewModel(ResetNowAsync, SaveSettingsAsync, OpenLogs);
        ViewModel.SetFinishPendingAction(FinishPendingAsync);
        ViewModel.SetExportDiagnosticsAction(() => ExportDiagnostics(openFolder: true));
        ViewModel.SetInstallUpdateAction(InstallUpdateAsync);
        ViewModel.LoadSettings(Options, _autostart.GetStatus(AutostartTarget.Desktop).Enabled);
        foreach (var warning in config.Warnings)
        {
            ViewModel.AddEvent($"Config: {warning}", time.GetUtcNow());
        }
    }

    public GuardOptions Options { get; private set; }

    public MainViewModel ViewModel { get; }

    public void Start(IDesktopUi ui)
    {
        _ui = ui;
        StartMonitor();
        if (_updates is not null && Options.Updates.CheckAutomatically)
        {
            _ = Task.Run(() => UpdateLoopAsync(_updateLoopStop.Token));
        }
    }

    /// <summary>Asks the app to exit (after an update was handed to the installer).</summary>
    public Action RequestExit { get; set; } = () => { };

    public static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    /// <summary>Checks once; shows and notifies (once per version) when a newer release exists.</summary>
    public async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        if (_updates is null)
        {
            return;
        }

        try
        {
            var check = await _updates.CheckAsync(cancellationToken).ConfigureAwait(false);
            LogUpdateChecked(_logger, check.CurrentVersion, check.Latest.Version);
            if (!check.UpdateAvailable)
            {
                return;
            }

            _availableUpdate = check;
            var canInstall = _updates.CanInstall && check.Package is not null && check.Checksums is not null;
            _ui?.Post(() => ViewModel.ShowUpdate(check.Latest.Version, check.Latest.PageUrl.ToString(), canInstall));

            if (_notifiedUpdateVersion != check.Latest.Version && _notifier.IsAvailable)
            {
                _notifiedUpdateVersion = check.Latest.Version;
                _ = _notifier.ShowAsync(
                    new Notification(NotificationKind.Info, $"ResetMe {check.Latest.Version} is available", "Open ResetMe to install the update."),
                    CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
        {
            // Offline or rate limited: try again next time, quietly.
            LogUpdateCheckFailed(_logger, ex);
        }
    }

    public async Task InstallUpdateAsync()
    {
        if (_updates is null || _availableUpdate is not { } check || _ui is null)
        {
            return;
        }

        var accepted = await _ui.AskAsync(
            $"Install ResetMe {check.Latest.Version}?",
            $"ResetMe downloads the release from GitHub, verifies it against SHA256SUMS.txt and restarts.\n\nInstalled: {check.CurrentVersion}\nNew: {check.Latest.Version}",
            "Install and restart",
            "Not now").ConfigureAwait(true);
        if (!accepted)
        {
            return;
        }

        var now = _time.GetUtcNow();
        try
        {
            var progress = new Progress<string>(text => ViewModel.UpdateText = text);
            await _updates.InstallAsync(check, progress, CancellationToken.None).ConfigureAwait(true);
            LogUpdateInstalled(_logger, check.Latest.Version);
            ViewModel.AddEvent($"Installing ResetMe {check.Latest.Version}; restarting.", now);
            RequestExit();
        }
        catch (Exception ex) when (ex is Platform.Updates.UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogUpdateFailed(_logger, ex);
            ViewModel.UpdateText = $"Update failed: {ex.Message}";
            ViewModel.AddEvent($"Update failed: {ex.Message}", now);
        }
    }

    private async Task UpdateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FirstUpdateCheckDelay, _time, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckForUpdateAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(UpdateCheckInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public Task ChangeModeAsync(GuardMode mode)
    {
        ViewModel.ModeIndex = (int)mode;
        return SaveSettingsAsync();
    }

    public async Task SaveSettingsAsync()
    {
        if (ViewModel.ModeIndex == (int)GuardMode.Automatic
            && Options.Mode != GuardMode.Automatic
            && !await ConfirmAutomaticModeAsync().ConfigureAwait(true))
        {
            ViewModel.ModeIndex = (int)Options.Mode;
            ViewModel.SettingsMessage = "Automatic mode was not enabled.";
            return;
        }

        try
        {
            Options = ViewModel.ApplySettings(Options);
            _configStore.Save(Options);
            _notifier = _notifierFactory(Options);
            await ApplyAutostartAsync(ViewModel.StartAtLogin).ConfigureAwait(true);
            LogSettingsSaved(_logger, Options.Mode.ToString(), Options.Monitor.IntervalSeconds);
            ViewModel.SettingsMessage = "Saved.";
            await RestartMonitorAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            LogSettingsFailed(_logger, ex);
            ViewModel.SettingsMessage = $"Could not save: {ex.Message}";
        }
    }

    /// <summary>Resumes the unresolved attempt with its original idempotency key (no new credit).</summary>
    public async Task FinishPendingAsync()
    {
        var now = _time.GetUtcNow();
        try
        {
            await using var connection = await _connector.ConnectAsync(CancellationToken.None).ConfigureAwait(true);
            var report = await CreateResetManager(connection)
                .ExecuteAsync(new ResetRequest(), CancellationToken.None)
                .ConfigureAwait(true);
            ReportReset(report, notify: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResetNowFailed(_logger, ex);
            ViewModel.AddEvent($"Finishing the pending reset failed: {ex.Message}", now);
        }

        if (_stateStore.Load().Pending is null)
        {
            ViewModel.ClearPendingAttempt();
        }
    }

    /// <summary>PRD §9.3: automatic mode must be enabled explicitly, knowing what it does.</summary>
    internal Task<bool> ConfirmAutomaticModeAsync()
    {
        if (_ui is null)
        {
            return Task.FromResult(false);
        }

        var o = Options;
        var message =
            "In automatic mode ResetMe redeems a reset credit as soon as Codex reports a limit, without asking you.\n\n"
            + "Safeguards that still apply:\n"
            + $"• at most {o.Automatic.MaxResetsPerDay} per day and {o.Automatic.MaxResetsPerWeek} per week\n"
            + $"• {o.Reset.CooldownSeconds}s cooldown between attempts\n"
            + $"• skipped when the limit lifts on its own within {o.Reset.MinTimeToNaturalResetMinutes} minutes\n"
            + "• never twice for the same limit, never for workspace limits, never unless Codex confirms the block\n\n"
            + "You get a notification after every automatic reset.";
        return _ui.AskAsync("Enable automatic mode?", message, "Enable automatic mode", "Cancel");
    }

    /// <summary>"Reset now" from the window or tray: fresh read, confirmation, then ResetManager.</summary>
    public async Task ResetNowAsync()
    {
        var now = _time.GetUtcNow();
        try
        {
            await using var connection = await _connector.ConnectAsync(CancellationToken.None).ConfigureAwait(true);
            var usage = await connection.GetUsageAsync(includeCreditDetails: true, CancellationToken.None).ConfigureAwait(true);
            var assessment = LimitEvaluator.Assess(usage, Options, now, ignoreNaturalResetThreshold: true);
            ViewModel.ApplyUsage(usage, assessment, now);

            if (!assessment.Blocked)
            {
                ViewModel.AddEvent("Codex is not rate-limited; nothing to reset.", now);
                return;
            }

            if (assessment.NoOfferReason is { } reason)
            {
                ViewModel.AddEvent($"Reset not possible: {reason}.", now);
                return;
            }

            // Recompute without overriding the threshold so the dialog can warn about it.
            var honest = LimitEvaluator.Assess(usage, Options, now);
            var notice = new LimitNotice(usage, honest, LimitHandling.AskUser);
            if (!await ConfirmAsync(notice).ConfigureAwait(true))
            {
                ViewModel.AddEvent("Reset declined.", now);
                return;
            }

            var report = await CreateResetManager(connection)
                .ExecuteAsync(new ResetRequest(Force: honest.NoOfferReason == NoOfferReason.NaturalResetSoon), CancellationToken.None)
                .ConfigureAwait(true);
            ReportReset(report, notify: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResetNowFailed(_logger, ex);
            ViewModel.AddEvent($"Reset failed: {ex.Message}", now);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _updateLoopStop.CancelAsync().ConfigureAwait(false);
        _updateLoopStop.Dispose();
        await StopMonitorAsync().ConfigureAwait(false);
        _dialogGate.Dispose();
    }

    internal async Task<bool> ConfirmAsync(LimitNotice notice)
    {
        if (_ui is null || !await _dialogGate.WaitAsync(0).ConfigureAwait(true))
        {
            return false; // One dialog at a time; a second request counts as "wait".
        }

        try
        {
            return await _ui.ConfirmAsync(new ConfirmViewModel(notice, _time.GetUtcNow())).ConfigureAwait(true);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    /// <param name="notify">False when the monitor's NotifyingObserver already sent the notification.</param>
    internal void ReportReset(ResetReport report, bool notify)
    {
        var now = _time.GetUtcNow();
        var text = NotificationTexts.ForReport(report);
        ViewModel.AddEvent($"{text.Title}: {text.Body}", now);
        if (report.UsageAfter is { } after)
        {
            ViewModel.ApplyUsage(after, LimitEvaluator.Assess(after, Options, now), now);
        }

        if (notify && _notifier.IsAvailable && report.Status is not (ResetRunStatus.NotBlocked or ResetRunStatus.LockBusy))
        {
            _ = _notifier.ShowAsync(text, CancellationToken.None);
        }
    }

    private ResetManager CreateResetManager(ICodexUsageClient client) =>
        new(client, _stateStore, _resetLock, Options, _time, _loggers.CreateLogger<ResetManager>());

    private void StartMonitor()
    {
        _monitorStop = new CancellationTokenSource();
        var observer = new NotifyingObserver(new DesktopObserver(this, _ui!, _time), _notifier, _time, NotificationTexts.DesktopAskHint);
        var monitor = new RateLimitMonitor(
            _connector,
            CreateResetManager,
            _stateStore,
            observer,
            Options,
            MonitorTiming.FromOptions(Options),
            _time,
            _loggers.CreateLogger<RateLimitMonitor>());
        var token = _monitorStop.Token;
        _monitorTask = Task.Run(() => monitor.RunAsync(token), CancellationToken.None);
    }

    private async Task StopMonitorAsync()
    {
        if (_monitorStop is null)
        {
            return;
        }

        await _monitorStop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _monitorTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _monitorStop.Dispose();
        _monitorStop = null;
    }

    private async Task RestartMonitorAsync()
    {
        await StopMonitorAsync().ConfigureAwait(true);
        if (_ui is not null)
        {
            StartMonitor();
        }
    }

    private async Task ApplyAutostartAsync(bool enabled)
    {
        var current = _autostart.GetStatus(AutostartTarget.Desktop).Enabled;
        if (enabled == current)
        {
            return;
        }

        if (enabled)
        {
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the ResetMe executable path.");
            await _autostart.EnableAsync(new AutostartEntry(AutostartTarget.Desktop, self, ["--minimized"]), CancellationToken.None).ConfigureAwait(true);
        }
        else
        {
            await _autostart.DisableAsync(AutostartTarget.Desktop, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private void OpenLogs() => OpenFolder(_paths.LogDirectory);

    /// <summary>Writes a sanitized diagnostics zip into the data directory and returns its path.</summary>
    public string? ExportDiagnostics(bool openFolder)
    {
        var now = _time.GetUtcNow();
        try
        {
            var folder = Path.Combine(_paths.Root, "diagnostics");
            var summary = new System.Text.StringBuilder()
                .AppendLine($"Status: {ViewModel.StatusText} {ViewModel.StatusDetail}")
                .AppendLine($"5-hour: {ViewModel.FiveHourDetailText} {ViewModel.FiveHourResetText}")
                .AppendLine($"Weekly: {ViewModel.WeeklyDetailText} {ViewModel.WeeklyResetText}")
                .AppendLine($"Credits: {ViewModel.CreditsText} {ViewModel.CreditExpiryText}")
                .AppendLine($"Mode: {Options.Mode}")
                .AppendLine($"Pending attempt: {ViewModel.HasPendingAttempt}")
                .AppendLine()
                .AppendLine("Recent events:")
                .AppendJoin(Environment.NewLine, ViewModel.Events)
                .ToString();

            var file = Platform.Diagnostics.DiagnosticsBundle.Create(
                _paths,
                Path.Combine(folder, Platform.Diagnostics.DiagnosticsBundle.DefaultFileName(now)),
                new Dictionary<string, string> { ["desktop.txt"] = summary },
                _time);
            ViewModel.AddEvent($"Diagnostics exported: {file}", now);
            if (openFolder)
            {
                OpenFolder(folder);
            }

            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ViewModel.AddEvent($"Diagnostics export failed: {ex.Message}", now);
            return null;
        }
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ViewModel.AddEvent($"Could not open {folder}: {ex.Message}", _time.GetUtcNow());
        }
    }

    [LoggerMessage(400, LogLevel.Information, "Settings saved (mode {Mode}, poll {IntervalSeconds}s)")]
    private static partial void LogSettingsSaved(ILogger logger, string mode, int intervalSeconds);

    [LoggerMessage(401, LogLevel.Warning, "Saving settings failed")]
    private static partial void LogSettingsFailed(ILogger logger, Exception error);

    [LoggerMessage(402, LogLevel.Error, "Manual reset failed")]
    private static partial void LogResetNowFailed(ILogger logger, Exception error);

    [LoggerMessage(403, LogLevel.Information, "Update check: installed {Current}, latest {Latest}")]
    private static partial void LogUpdateChecked(ILogger logger, string current, string latest);

    [LoggerMessage(404, LogLevel.Debug, "Update check failed")]
    private static partial void LogUpdateCheckFailed(ILogger logger, Exception error);

    [LoggerMessage(405, LogLevel.Information, "Update {Version} handed to the installer; exiting")]
    private static partial void LogUpdateInstalled(ILogger logger, string version);

    [LoggerMessage(406, LogLevel.Warning, "Update failed")]
    private static partial void LogUpdateFailed(ILogger logger, Exception error);
}

/// <summary>Monitor events → view model (on the UI thread) and the confirmation dialog.</summary>
internal sealed class DesktopObserver(DesktopHost host, IDesktopUi ui, TimeProvider time) : IMonitorObserver
{
    // Starts "offline" so the first successful read records "Connected to Codex.".
    private bool _offline = true;

    public void OnUsage(CodexUsage usage, LimitAssessment assessment)
    {
        var now = time.GetUtcNow();
        ui.Post(() =>
        {
            if (_offline)
            {
                host.ViewModel.AddEvent("Connected to Codex.", now);
                _offline = false;
            }

            host.ViewModel.ApplyUsage(usage, assessment, now);
        });
    }

    public void OnLimitReached(LimitNotice notice)
    {
        var text = NotificationTexts.ForLimit(notice, time.GetUtcNow());
        ui.Post(() => host.ViewModel.AddEvent($"{text.Title}. {text.Body}", time.GetUtcNow()));
    }

    public void OnPendingAttempt(PendingResetAttempt pending) => ui.Post(() =>
    {
        host.ViewModel.ShowPendingAttempt(pending.StartedAt);
        host.ViewModel.AddEvent("An earlier reset attempt has no confirmed result.", time.GetUtcNow());
    });

    public void OnNearLimit(NearLimitNotice notice, CodexUsage usage)
    {
        var text = NotificationTexts.ForNearLimit(notice, usage.AvailableResetCount, time.GetUtcNow());
        ui.Post(() => host.ViewModel.AddEvent($"{text.Title}. {text.Body}", time.GetUtcNow()));
    }

    public async Task<bool> ConfirmResetAsync(LimitNotice notice, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(async () => tcs.TrySetResult(await host.ConfirmAsync(notice).ConfigureAwait(true)));
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        var accepted = await tcs.Task.ConfigureAwait(false);
        ui.Post(() => host.ViewModel.AddEvent(accepted ? "Reset accepted." : "Waiting; not asking again for this limit.", time.GetUtcNow()));
        return accepted;
    }

    public void OnResetCompleted(ResetReport report) => ui.Post(() => host.ReportReset(report, notify: false));

    public void OnResetFailed(Exception error) =>
        ui.Post(() => host.ViewModel.AddEvent($"Reset failed: {error.Message}. Run `resetme reset` to finish it.", time.GetUtcNow()));

    public void OnAuthRequired() => ui.Post(() =>
    {
        host.ViewModel.SetOffline("Not logged in", "Run `codex login`, then ResetMe reconnects.");
        _offline = true;
    });

    public void OnUnavailable(Exception error, TimeSpan retryIn) => ui.Post(() =>
    {
        if (!_offline)
        {
            host.ViewModel.AddEvent($"Codex unavailable: {error.Message}", time.GetUtcNow());
        }

        host.ViewModel.SetOffline("Codex unavailable", $"Retrying in {retryIn.TotalSeconds:0}s.");
        _offline = true;
    });
}
