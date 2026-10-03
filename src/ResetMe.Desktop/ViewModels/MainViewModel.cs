using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResetMe.Core.Domain;
using ResetMe.Core.Monitoring;
using ResetMe.Core.Policies;

namespace ResetMe.Desktop.ViewModels;

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
    private readonly Func<Task> _saveSettings;
    private readonly Action _openLogs;

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

    [ObservableProperty]
    public partial string SettingsMessage { get; set; } = "";

    /// <summary>Highest of the two windows; drives the tray icon ring.</summary>
    public double PeakPercent => Math.Max(FiveHourPercent, WeeklyPercent);

    public string TrayToolTip => Health switch
    {
        HealthKind.Offline => $"ResetMe — {StatusText}",
        HealthKind.Starting => "ResetMe — connecting…",
        _ => $"ResetMe — 5h {FiveHourText} · weekly {WeeklyText} · resets {CreditsText}",
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
        WeeklyPercent = Clamp(usage.Weekly?.UsedPercent);
        WeeklyText = Percent(usage.Weekly);
        WeeklyResetText = ResetsIn(usage.Weekly, now);
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
        return current;
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
            ? $"ResetMe {version} is available. Installing restarts ResetMe."
            : $"ResetMe {version} is available: {pageUrl}";
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
