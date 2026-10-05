using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fermata.Core.Domain;
using Fermata.Core.Monitoring;
using Fermata.Core.Policies;

namespace Fermata.Desktop.ViewModels;

/// <summary>Traffic-light state shared by the window and the tray icon.</summary>
public enum HealthKind
{
    Starting,
    Ok,
    Warning,
    Blocked,
    Offline,
}

/// <summary>
/// Everything the window and tray show (PRD §11, §37). UI-framework free so it can be unit tested;
/// the host injects the actions behind the commands.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const int MaxEvents = 50;
    public const double WarningPercent = 80;

    private readonly Func<Task> _resetNow;
    private Func<Task> _finishPending = () => Task.CompletedTask;
    private Action _exportDiagnostics = () => { };
    private Func<Task> _installUpdate = () => Task.CompletedTask;
    private Func<GuardMode, Task> _changeMode = _ => Task.CompletedTask;
    private readonly Func<Task> _saveSettings;
    private readonly Action _openLogs;
    private GuardMode _savedMode = GuardMode.Confirm;

    public MainViewModel(Func<Task> resetNow, Func<Task> saveSettings, Action openLogs)
    {
        _resetNow = resetNow;
        _saveSettings = saveSettings;
        _openLogs = openLogs;
    }

    public ObservableCollection<string> Events { get; } = [];

    /// <summary>Zone used for wall-clock reset times; tests pin it to UTC.</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    public static IReadOnlyList<string> ModeNames { get; } = ["Manual", "Confirm", "Automatic"];

    [ObservableProperty]
    public partial double FiveHourPercent { get; set; }

    [ObservableProperty]
    public partial string FiveHourText { get; set; } = "–";

    [ObservableProperty]
    public partial string FiveHourResetText { get; set; } = "";

    /// <summary>"82% used · 18% left" (Codex itself reports the remaining share).</summary>
    [ObservableProperty]
    public partial string FiveHourDetailText { get; set; } = "";

    [ObservableProperty]
    public partial string FiveHourLeftText { get; set; } = "";

    /// <summary>"2h 27m" — compact form for the tray flyout.</summary>
    [ObservableProperty]
    public partial string FiveHourResetsInText { get; set; } = "";

    [ObservableProperty]
    public partial double WeeklyPercent { get; set; }

    [ObservableProperty]
    public partial string WeeklyText { get; set; } = "–";

    [ObservableProperty]
    public partial string WeeklyResetText { get; set; } = "";

    [ObservableProperty]
    public partial string WeeklyDetailText { get; set; } = "";

    [ObservableProperty]
    public partial string WeeklyLeftText { get; set; } = "";

    [ObservableProperty]
    public partial string WeeklyResetsInText { get; set; } = "";

    [ObservableProperty]
    public partial string CreditsText { get; set; } = "–";

    [ObservableProperty]
    public partial string CreditExpiryText { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Connecting to Codex…";

    [ObservableProperty]
    public partial string StatusDetail { get; set; } = "";

    [ObservableProperty]
    public partial HealthKind Health { get; set; } = HealthKind.Starting;

    [ObservableProperty]
    public partial string LastUpdatedText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetNowCommand))]
    public partial bool CanResetNow { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(FinishPendingCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>An earlier attempt has no confirmed result; offer to finish it with the same key.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FinishPendingCommand))]
    public partial bool HasPendingAttempt { get; set; }

    [ObservableProperty]
    public partial string PendingText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    public partial bool UpdateAvailable { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    public partial bool CanInstallUpdate { get; set; }

    [ObservableProperty]
    public partial string UpdateVersion { get; set; } = "";

    [ObservableProperty]
    public partial string UpdateText { get; set; } = "";

    // Settings (PRD §20)
    [ObservableProperty]
    public partial int ModeIndex { get; set; } = 1;

    [ObservableProperty]
    public partial decimal IntervalSeconds { get; set; } = 30;

    [ObservableProperty]
    public partial decimal MinNaturalResetMinutes { get; set; } = 15;

    [ObservableProperty]
    public partial bool NotificationsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool NearLimitEnabled { get; set; } = true;

    /// <summary>"Warn at 80%, 90%, 95%" — label for the near-limit checkbox.</summary>
    [ObservableProperty]
    public partial string NearLimitLabel { get; set; } = "Warn when usage reaches 80%, 90%, 95%";

    [ObservableProperty]
    public partial bool StartAtLogin { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; } = true;

    /// <summary>Resume Codex goals that stopped at the limit after a reset.</summary>
    [ObservableProperty]
    public partial bool ContinueAfterReset { get; set; } = true;

    [ObservableProperty]
    public partial string SettingsMessage { get; set; } = "";

    /// <summary>Fermata's hooks are in Codex's hooks.json.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodexHooksButtonText))]
    public partial bool CodexHooksInstalled { get; set; }

    [ObservableProperty]
    public partial string CodexHooksText { get; set; } = "";

    public string CodexHooksButtonText => CodexHooksInstalled ? "Remove from Codex" : "Add to Codex";

    /// <summary>Highest of the two windows; drives the tray icon ring.</summary>
    public double PeakPercent => Math.Max(FiveHourPercent, WeeklyPercent);

    public string TrayToolTip => Health switch
    {
        HealthKind.Offline => $"Fermata — {StatusText}",
        HealthKind.Starting => "Fermata — connecting…",
        _ => $"Fermata — 5h {FiveHourText} · weekly {WeeklyText} · resets {CreditsText}",
    };

    public void ApplyUsage(CodexUsage usage, LimitAssessment assessment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(assessment);

        FiveHourPercent = Clamp(usage.FiveHour?.UsedPercent);
        FiveHourText = Percent(usage.FiveHour);
        FiveHourResetText = ResetsIn(usage.FiveHour, now);
        FiveHourLeftText = Left(usage.FiveHour);
        FiveHourDetailText = Detail(usage.FiveHour);
        FiveHourResetsInText = ShortResetsIn(usage.FiveHour, now);
        WeeklyPercent = Clamp(usage.Weekly?.UsedPercent);
        WeeklyText = Percent(usage.Weekly);
        WeeklyResetText = ResetsIn(usage.Weekly, now);
        WeeklyResetsInText = ShortResetsIn(usage.Weekly, now);
        WeeklyLeftText = Left(usage.Weekly);
        WeeklyDetailText = Detail(usage.Weekly);
        CreditsText = usage.ResetCreditsReported ? usage.AvailableResetCount.ToString(CultureInfo.InvariantCulture) : "n/a";
        CreditExpiryText = LimitEvaluator.SelectCredit(usage.Credits)?.ExpiresAt is { } expires
            ? $"next expires in {NotificationTexts.Duration(expires - now)}"
            : "";

        if (assessment.Blocked)
        {
            Health = HealthKind.Blocked;
            StatusText = "Limit reached";
            StatusDetail = assessment.NoOfferReason switch
            {
                null => "A reset credit can be used.",
                NoOfferReason.NoCredit => "No reset credits left.",
                NoOfferReason.NaturalResetSoon when assessment.NaturalUnblockAt is { } at => $"Lifts on its own in {NotificationTexts.Duration(at - now)}.",
                NoOfferReason.WorkspaceLimit => "Workspace limit: reset credits do not apply.",
                NoOfferReason.WindowDisabled => "This window is disabled in settings.",
                _ => "",
            };
        }
        else
        {
            Health = PeakPercent >= WarningPercent ? HealthKind.Warning : HealthKind.Ok;
            StatusText = usage.UsageAllowed is null ? "Monitoring (usage flag not reported)" : "Monitoring";
            StatusDetail = "";
        }

        CanResetNow = assessment.Blocked && usage.AvailableResetCount > 0 && !usage.IsWorkspaceLimit;
        LastUpdatedText = $"Updated {now.ToLocalTime():HH:mm:ss}";
        OnPropertyChanged(nameof(PeakPercent));
        OnPropertyChanged(nameof(TrayToolTip));
    }

    public void SetOffline(string status, string detail)
    {
        Health = HealthKind.Offline;
        StatusText = status;
        StatusDetail = detail;
        CanResetNow = false;
        OnPropertyChanged(nameof(TrayToolTip));
    }

    public void AddEvent(string text, DateTimeOffset at)
    {
        Events.Insert(0, $"{at.ToLocalTime():HH:mm}  {text}");
        while (Events.Count > MaxEvents)
        {
            Events.RemoveAt(Events.Count - 1);
        }
    }

    public void LoadSettings(GuardOptions options, bool startAtLogin)
    {
        ArgumentNullException.ThrowIfNull(options);
        ModeIndex = (int)options.Mode;
        IntervalSeconds = options.Monitor.IntervalSeconds;
        MinNaturalResetMinutes = options.Reset.MinTimeToNaturalResetMinutes;
        NotificationsEnabled = options.NotificationsEnabled;
        NearLimitEnabled = options.NearLimit.Enabled;
        NearLimitLabel = options.NearLimit.Thresholds.Count == 0
            ? "Near-limit warnings (no thresholds configured)"
            : "Warn when usage reaches " + string.Join(", ", options.NearLimit.Thresholds.Select(t => $"{t}%"));
        StartMinimized = options.StartMinimized;
        StartAtLogin = startAtLogin;
        ContinueAfterReset = options.ContinueAfterReset;
        SetSavedMode(options.Mode);
    }

    /// <summary>Copies the editable settings onto <paramref name="current"/>, keeping everything else.</summary>
    public GuardOptions ApplySettings(GuardOptions current)
    {
        ArgumentNullException.ThrowIfNull(current);
        current.Mode = (GuardMode)Math.Clamp(ModeIndex, 0, 2);
        current.Monitor.IntervalSeconds = Math.Max(MonitorOptions.MinimumIntervalSeconds, (int)IntervalSeconds);
        current.Reset.MinTimeToNaturalResetMinutes = Math.Max(0, (int)MinNaturalResetMinutes);
        current.NotificationsEnabled = NotificationsEnabled;
        current.NearLimit.Enabled = NearLimitEnabled;
        current.StartMinimized = StartMinimized;
        current.ContinueAfterReset = ContinueAfterReset;
        SetSavedMode(current.Mode);
        return current;
    }

    /// <summary>
    /// Flyout switch: on means automatic mode, off means confirm. Reflects the saved mode, not the
    /// unsaved settings combo box. Turning it on still goes through the host's explicit confirmation.
    /// </summary>
    public bool AutoReset
    {
        get => _savedMode == GuardMode.Automatic;
        set
        {
            if (value != AutoReset)
            {
                _ = SetAutoResetAsync(value);
            }
        }
    }

    /// <summary>Wires the mode change used by the flyout switch (set by the host).</summary>
    public void SetChangeModeAction(Func<GuardMode, Task> action) => _changeMode = action;

    private async Task SetAutoResetAsync(bool enabled)
    {
        try
        {
            await _changeMode(enabled ? GuardMode.Automatic : GuardMode.Confirm).ConfigureAwait(true);
        }
        finally
        {
            // Declined or failed: the switch snaps back to the saved mode.
            OnPropertyChanged(nameof(AutoReset));
        }
    }

    private void SetSavedMode(GuardMode mode)
    {
        _savedMode = mode;
        OnPropertyChanged(nameof(AutoReset));
    }

    partial void OnHealthChanged(HealthKind value) => OnPropertyChanged(nameof(TrayToolTip));

    [RelayCommand(CanExecute = nameof(CanExecuteResetNow))]
    private async Task ResetNowAsync()
    {
        IsBusy = true;
        try
        {
            await _resetNow().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExecuteResetNow() => CanResetNow && !IsBusy;

    public void ShowUpdate(string version, string pageUrl, bool canInstall)
    {
        UpdateVersion = version;
        CanInstallUpdate = canInstall;
        UpdateText = canInstall
            ? $"Fermata {version} is available. Installing restarts Fermata."
            : $"Fermata {version} is available: {pageUrl}";
        UpdateAvailable = true;
    }

    public void SetInstallUpdateAction(Func<Task> action) => _installUpdate = action;

    [RelayCommand(CanExecute = nameof(CanExecuteInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        IsBusy = true;
        try
        {
            await _installUpdate().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExecuteInstallUpdate() => UpdateAvailable && CanInstallUpdate && !IsBusy;

    // ── Jobs ────────────────────────────────────────────────────────────────

    private Func<string, Task> _pauseJob = _ => Task.CompletedTask;
    private Func<string, Task> _resumeJob = _ => Task.CompletedTask;
    private Action _openClaudeLimitResets = () => { };
    private Func<Task> _toggleClaudeIntegration = () => Task.CompletedTask;

    /// <summary>Open jobs (and those finished in the last day), newest first.</summary>
    public ObservableCollection<JobRowViewModel> Jobs { get; } = [];

    [ObservableProperty]
    public partial bool HasJobs { get; set; }

    /// <summary>A Claude job waits for the quota: offer the claude.ai "Limit resets" page.</summary>
    [ObservableProperty]
    public partial bool ShowClaudeLimitResets { get; set; }

    [ObservableProperty]
    public partial string JobsSummary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClaudeIntegrationButtonText))]
    public partial bool ClaudeIntegrationInstalled { get; set; }

    [ObservableProperty]
    public partial string ClaudeIntegrationText { get; set; } = "";

    public string ClaudeIntegrationButtonText => ClaudeIntegrationInstalled ? "Remove from Claude Code" : "Add to Claude Code";

    public void SetJobActions(Func<string, Task> pause, Func<string, Task> resume, Action openClaudeLimitResets)
    {
        _pauseJob = pause;
        _resumeJob = resume;
        _openClaudeLimitResets = openClaudeLimitResets;
    }

    public void SetToggleClaudeIntegrationAction(Func<Task> action) => _toggleClaudeIntegration = action;

    public void ApplyJobs(IReadOnlyList<Fermata.Core.Jobs.Job> jobs, Func<string, IReadOnlyList<Fermata.Core.Jobs.JobEvent>> events, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(events);
        var shown = jobs
            .Where(j => !j.IsFinished || j.FinishedAt > now.AddDays(-1))
            .OrderByDescending(j => j.IsFinished ? 0 : 1)
            .ThenByDescending(j => j.UpdatedAt)
            .Select(j => new JobRowViewModel(j, events(j.Id), now, TimeZone, _pauseJob, _resumeJob))
            .ToList();

        Jobs.Clear();
        foreach (var row in shown)
        {
            Jobs.Add(row);
        }

        HasJobs = Jobs.Count > 0;
        ShowClaudeLimitResets = Jobs.Any(j => j.IsClaudeWaiting);
        var open = jobs.Count(j => !j.IsFinished);
        var attention = Jobs.Count(j => j.NeedsAttention);
        JobsSummary = open == 0 ? "" : attention > 0 ? $"{open} job(s), {attention} need you" : $"{open} job(s)";
    }

    [RelayCommand]
    private void OpenClaudeLimitResets() => _openClaudeLimitResets();

    [RelayCommand]
    private Task ToggleClaudeIntegrationAsync() => _toggleClaudeIntegration();

    private Func<Task> _toggleCodexHooks = () => Task.CompletedTask;

    /// <summary>Wires the Codex hooks install/remove action (set by the host).</summary>
    public void SetToggleCodexHooksAction(Func<Task> action) => _toggleCodexHooks = action;

    [RelayCommand]
    private Task ToggleCodexHooksAsync() => _toggleCodexHooks();

    /// <summary>Wires the "Export diagnostics" action (set by the host).</summary>
    public void SetExportDiagnosticsAction(Action action) => _exportDiagnostics = action;

    [RelayCommand]
    private void ExportDiagnostics() => _exportDiagnostics();

    /// <summary>Wires the "Finish pending reset" action (set by the host).</summary>
    public void SetFinishPendingAction(Func<Task> action) => _finishPending = action;

    public void ShowPendingAttempt(DateTimeOffset startedAt)
    {
        HasPendingAttempt = true;
        PendingText = $"A reset attempt from {startedAt.ToLocalTime():g} has no confirmed result. Finishing it reuses the same key, so it cannot use a second credit.";
    }

    public void ClearPendingAttempt()
    {
        HasPendingAttempt = false;
        PendingText = "";
    }

    [RelayCommand(CanExecute = nameof(CanFinishPending))]
    private async Task FinishPendingAsync()
    {
        IsBusy = true;
        try
        {
            await _finishPending().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanFinishPending() => HasPendingAttempt && !IsBusy;

    [RelayCommand]
    private Task SaveSettingsAsync() => _saveSettings();

    [RelayCommand]
    private void OpenLogs() => _openLogs();

    private static double Clamp(double? percent) => Math.Clamp(percent ?? 0, 0, 100);

    private static string Percent(UsageWindow? window) =>
        window is null ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{window.UsedPercent:0}%");

    private static string Left(UsageWindow? window) =>
        window is null ? "" : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, 100 - window.UsedPercent):0}% left");

    private static string Detail(UsageWindow? window) =>
        window is null ? "" : $"{Percent(window)} used · {Left(window)}";

    private static string ShortResetsIn(UsageWindow? window, DateTimeOffset now) =>
        window?.ResetsAt is { } at ? NotificationTexts.Duration(at - now) : "";

    /// <summary>"resets in 3h 3m (02:31)", or "(02:31 on 3 Oct)" when it is not today.</summary>
    private string ResetsIn(UsageWindow? window, DateTimeOffset now)
    {
        if (window?.ResetsAt is not { } at)
        {
            return "";
        }

        var local = TimeZoneInfo.ConvertTime(at, TimeZone);
        var today = TimeZoneInfo.ConvertTime(now, TimeZone).Date;
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date != today)
        {
            clock += " on " + local.ToString("d MMM", CultureInfo.InvariantCulture);
        }

        return $"resets in {NotificationTexts.Duration(at - now)} ({clock})";
    }
}
