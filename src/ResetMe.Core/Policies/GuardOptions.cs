namespace ResetMe.Core.Policies;

public enum GuardMode
{
    Manual,
    Confirm,
    Automatic,
}

/// <summary>In-memory form of config.toml (PRD §20). Defaults match the PRD.</summary>
public sealed class GuardOptions
{
    public GuardMode Mode { get; set; } = GuardMode.Confirm;

    public MonitorOptions Monitor { get; set; } = new();

    public LimitOptions Limits { get; set; } = new();

    public ResetOptions Reset { get; set; } = new();

    public AutomaticOptions Automatic { get; set; } = new();

    public NearLimitOptions NearLimit { get; set; } = new();

    public UpdateOptions Updates { get; set; } = new();

    public LoggingOptions Logging { get; set; } = new();

    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Desktop app starts hidden in the tray / menu bar.</summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>Path to the codex executable; empty means search PATH.</summary>
    public string CodexExecutable { get; set; } = "";
}

public sealed class MonitorOptions
{
    public const int MinimumIntervalSeconds = 10;

    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 30;
}

public sealed class LimitOptions
{
    /// <summary>Offer a reset when the 5-hour window is the one blocking.</summary>
    public bool FiveHour { get; set; } = true;

    /// <summary>Offer a reset when the weekly window is the one blocking.</summary>
    public bool Weekly { get; set; } = true;
}

public sealed class ResetOptions
{
    public int CooldownSeconds { get; set; } = 120;

    public int VerifyAfterSeconds { get; set; } = 3;

    public int VerifyIntervalSeconds { get; set; } = 5;

    public int VerifyTimeoutSeconds { get; set; } = 120;

    public int MinTimeToNaturalResetMinutes { get; set; } = 15;

    /// <summary>Extra consume attempts with the same idempotency key after a transient failure.</summary>
    public int ConsumeRetryMax { get; set; } = 3;

    public int ConsumeRetryDelaySeconds { get; set; } = 2;
}

public sealed class LoggingOptions
{
    /// <summary>Minimum level written to the log file (PRD §30: Information by default).</summary>
    public Microsoft.Extensions.Logging.LogLevel Level { get; set; } = Microsoft.Extensions.Logging.LogLevel.Information;

    public int RetentionDays { get; set; } = 14;
}

/// <summary>Self-update (PRD MVP-3). The check reads the public GitHub release feed; it sends no user data.</summary>
public sealed class UpdateOptions
{
    public bool CheckAutomatically { get; set; } = true;
}

/// <summary>Warnings before a window is exhausted (PRD §33).</summary>
public sealed class NearLimitOptions
{
    public bool Enabled { get; set; } = true;

    public List<int> Thresholds { get; set; } = [80, 90, 95];
}

public sealed class AutomaticOptions
{
    public int MaxResetsPerDay { get; set; } = 1;

    public int MaxResetsPerWeek { get; set; } = 2;
}
