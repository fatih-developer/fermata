namespace Fermata.Core.Policies;

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

    /// <summary>
    /// After a successful reset, set Codex goals that stopped at the usage limit back to active so
    /// unattended work continues.
    /// </summary>
    public bool ContinueAfterReset { get; set; } = true;

    public JobsOptions Jobs { get; set; } = new();
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

/// <summary>Supervised jobs (<c>fermata run/adopt</c>): when to checkpoint, stop and resume.</summary>
public sealed class JobsOptions
{
    /// <summary>Ask for a handoff note when a window has this many percent left.</summary>
    public int PrepareRemaining { get; set; } = 10;

    /// <summary>Stop after the current turn when a window has this many percent left (0 = only at the limit).</summary>
    public int StopRemaining { get; set; } = 5;

    /// <summary>Wait this long after the reset time before resuming.</summary>
    public int GraceSeconds { get; set; } = 90;

    /// <summary>Also store `git diff` in each checkpoint.</summary>
    public bool SavePatch { get; set; }

    public CodexJobsOptions Codex { get; set; } = new();

    public ClaudeJobsOptions Claude { get; set; } = new();

    public Fermata.Core.Jobs.QuotaPolicyOptions QuotaPolicy => new(PrepareRemaining, StopRemaining, GraceSeconds);
}

public sealed class CodexJobsOptions
{
    /// <summary>Who answers approvals in jobs Fermata starts: "user" (your Codex client) or "auto_review" (Codex reviewer).</summary>
    public string Approvals { get; set; } = "user";

    /// <summary>Model for threads Fermata starts; empty keeps your Codex default.</summary>
    public string Model { get; set; } = "";
}

public sealed class ClaudeJobsOptions
{
    /// <summary>Path to the claude executable; empty means search PATH.</summary>
    public string Executable { get; set; } = "";

    /// <summary>Permission mode for jobs Fermata starts. Never bypassPermissions by default.</summary>
    public string PermissionMode { get; set; } = "default";

    /// <summary>Suggest a reset credit (claude.ai) when the wait would be longer than this.</summary>
    public int SuggestResetAfterMinutes { get; set; } = 120;

    /// <summary>The claude.ai page with "Limit resets".</summary>
    public string LimitResetsUrl { get; set; } = "https://claude.ai/settings/usage";
}
