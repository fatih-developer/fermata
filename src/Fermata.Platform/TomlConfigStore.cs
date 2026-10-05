using System.Text.Json.Serialization;
using Fermata.Core.Policies;
using Tomlyn;
using Tomlyn.Serialization;

namespace Fermata.Platform;

public sealed record ConfigLoadResult(GuardOptions Options, IReadOnlyList<string> Warnings, bool FileExists);

/// <summary>Reads and creates config.toml (PRD §20).</summary>
public sealed class TomlConfigStore
{
    private readonly string _path;

    public TomlConfigStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public ConfigLoadResult Load()
    {
        if (!File.Exists(_path))
        {
            return new ConfigLoadResult(new GuardOptions(), [], FileExists: false);
        }

        var file = TomlSerializer.Deserialize(File.ReadAllText(_path), ConfigTomlContext.Default.ConfigFile)
            ?? new ConfigFile();
        var warnings = new List<string>();
        return new ConfigLoadResult(Map(file, warnings), warnings, FileExists: true);
    }

    /// <summary>Writes the default, commented config if none exists. Returns true when created.</summary>
    public bool EnsureDefault()
    {
        if (File.Exists(_path))
        {
            return false;
        }

        File.WriteAllText(_path, DefaultToml);
        FilePermissions.RestrictToCurrentUser(_path, isDirectory: false);
        return true;
    }

    /// <summary>
    /// Writes <paramref name="options"/> as a complete, commented config.toml (atomic replace).
    /// Hand-written comments are not preserved; the template's comments are.
    /// </summary>
    public void Save(GuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temp = System.IO.Path.Combine(directory, $".config.{Environment.ProcessId}.tmp");
        File.WriteAllText(temp, Render(options));
        FilePermissions.RestrictToCurrentUser(temp, isDirectory: false);
        File.Move(temp, _path, overwrite: true);
    }

    internal static string Render(GuardOptions o)
    {
        static string B(bool value) => value ? "true" : "false";
        static string S(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        static string L(string value) => value.ToLowerInvariant();

        return $"""
            # Fermata configuration
            # mode: "manual" | "confirm" | "automatic"   (automatic must be enabled explicitly)
            mode = {S(L(o.Mode.ToString()))}

            [monitor]
            enabled = {B(o.Monitor.Enabled)}
            interval_seconds = {o.Monitor.IntervalSeconds}          # minimum 10

            [limits]
            # Offer a reset when this window is the one blocking you.
            five_hour = {B(o.Limits.FiveHour)}
            weekly = {B(o.Limits.Weekly)}

            [notifications]
            enabled = {B(o.NotificationsEnabled)}

            [reset]
            cooldown_seconds = {o.Reset.CooldownSeconds}
            verify_after_seconds = {o.Reset.VerifyAfterSeconds}
            verify_interval_seconds = {o.Reset.VerifyIntervalSeconds}
            verify_timeout_seconds = {o.Reset.VerifyTimeoutSeconds}
            # Do not offer a reset if the limit lifts on its own sooner than this.
            min_time_to_natural_reset_minutes = {o.Reset.MinTimeToNaturalResetMinutes}
            # Extra consume tries with the SAME idempotency key after a timeout.
            consume_retry_max = {o.Reset.ConsumeRetryMax}

            [near_limit]
            # Warn once per threshold while a window fills up (percent used).
            enabled = {B(o.NearLimit.Enabled)}
            thresholds = [{string.Join(", ", o.NearLimit.Thresholds)}]

            [updates]
            # Once a day the desktop app reads the public GitHub release feed (no user data is sent).
            check_automatically = {B(o.Updates.CheckAutomatically)}

            [automatic]
            max_resets_per_day = {o.Automatic.MaxResetsPerDay}
            max_resets_per_week = {o.Automatic.MaxResetsPerWeek}

            [logging]
            # error | warning | information | debug | trace
            level = {S(L(o.Logging.Level.ToString()))}
            retention_days = {o.Logging.RetentionDays}

            [codex]
            executable = {S(o.CodexExecutable)}                # empty = search PATH
            # After a reset, resume Codex goals (/goal) that stopped at the usage limit.
            continue_after_reset = {B(o.ContinueAfterReset)}

            [jobs]
            # Supervised jobs (`fermata run`, `fermata adopt`). Percent LEFT in the fullest window:
            prepare_remaining = {o.Jobs.PrepareRemaining}            # ask the agent for a handoff note
            stop_remaining = {o.Jobs.StopRemaining}               # stop after the current turn (0 = only at the limit)
            grace_seconds = {o.Jobs.GraceSeconds}               # wait this long after the reset time before resuming
            save_patch = {B(o.Jobs.SavePatch)}              # also store `git diff` in each checkpoint

            [jobs.codex]
            approvals = {S(o.Jobs.Codex.Approvals)}             # "user" (your Codex client answers) | "auto_review"
            model = {S(o.Jobs.Codex.Model)}                     # empty = your Codex default

            [jobs.claude]
            executable = {S(o.Jobs.Claude.Executable)}                # empty = search PATH
            permission_mode = {S(o.Jobs.Claude.PermissionMode)}      # for jobs Fermata starts; never bypass by default
            suggest_reset_after_minutes = {o.Jobs.Claude.SuggestResetAfterMinutes}  # suggest a claude.ai reset credit for longer waits
            limit_resets_url = {S(o.Jobs.Claude.LimitResetsUrl)}

            [ui]
            start_minimized = {B(o.StartMinimized)}         # desktop app starts in the tray / menu bar

            """;
    }

    internal static GuardOptions Map(ConfigFile file, List<string> warnings)
    {
        var options = new GuardOptions();

        if (file.Mode is { } mode)
        {
            if (Enum.TryParse<GuardMode>(mode, ignoreCase: true, out var parsed))
            {
                options.Mode = parsed;
            }
            else
            {
                warnings.Add($"Unknown mode '{mode}'; using 'confirm'.");
            }
        }

        if (file.Monitor is { } monitor)
        {
            options.Monitor.Enabled = monitor.Enabled ?? options.Monitor.Enabled;
            if (monitor.IntervalSeconds is { } interval)
            {
                if (interval < MonitorOptions.MinimumIntervalSeconds)
                {
                    warnings.Add($"monitor.interval_seconds={interval} is below the minimum; using {MonitorOptions.MinimumIntervalSeconds}.");
                    interval = MonitorOptions.MinimumIntervalSeconds;
                }

                options.Monitor.IntervalSeconds = interval;
            }
        }

        if (file.Limits is { } limits)
        {
            options.Limits.FiveHour = limits.FiveHour ?? options.Limits.FiveHour;
            options.Limits.Weekly = limits.Weekly ?? options.Limits.Weekly;
        }

        options.NotificationsEnabled = file.Notifications?.Enabled ?? options.NotificationsEnabled;

        if (file.Reset is { } reset)
        {
            var r = options.Reset;
            r.CooldownSeconds = NonNegative(reset.CooldownSeconds, r.CooldownSeconds, "reset.cooldown_seconds", warnings);
            r.VerifyAfterSeconds = NonNegative(reset.VerifyAfterSeconds, r.VerifyAfterSeconds, "reset.verify_after_seconds", warnings);
            r.VerifyIntervalSeconds = NonNegative(reset.VerifyIntervalSeconds, r.VerifyIntervalSeconds, "reset.verify_interval_seconds", warnings);
            r.VerifyTimeoutSeconds = NonNegative(reset.VerifyTimeoutSeconds, r.VerifyTimeoutSeconds, "reset.verify_timeout_seconds", warnings);
            r.MinTimeToNaturalResetMinutes = NonNegative(reset.MinTimeToNaturalResetMinutes, r.MinTimeToNaturalResetMinutes, "reset.min_time_to_natural_reset_minutes", warnings);
            r.ConsumeRetryMax = NonNegative(reset.ConsumeRetryMax, r.ConsumeRetryMax, "reset.consume_retry_max", warnings);
        }

        if (file.NearLimit is { } near)
        {
            options.NearLimit.Enabled = near.Enabled ?? options.NearLimit.Enabled;
            if (near.Thresholds is { } thresholds)
            {
                var valid = thresholds.Where(t => t is > 0 and < 100).Distinct().Order().ToList();
                if (valid.Count != thresholds.Count)
                {
                    warnings.Add("near_limit.thresholds must be distinct values between 1 and 99; invalid entries ignored.");
                }

                options.NearLimit.Thresholds = valid;
            }
        }

        options.Updates.CheckAutomatically = file.Updates?.CheckAutomatically ?? options.Updates.CheckAutomatically;

        if (file.Automatic is { } automatic)
        {
            var a = options.Automatic;
            a.MaxResetsPerDay = NonNegative(automatic.MaxResetsPerDay, a.MaxResetsPerDay, "automatic.max_resets_per_day", warnings);
            a.MaxResetsPerWeek = NonNegative(automatic.MaxResetsPerWeek, a.MaxResetsPerWeek, "automatic.max_resets_per_week", warnings);
        }

        if (file.Logging is { } logging)
        {
            if (logging.Level is { } level)
            {
                if (Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(level, ignoreCase: true, out var parsed)
                    && parsed != Microsoft.Extensions.Logging.LogLevel.None)
                {
                    options.Logging.Level = parsed;
                }
                else
                {
                    warnings.Add($"Unknown logging.level '{level}'; using 'information'.");
                }
            }

            var retention = NonNegative(logging.RetentionDays, options.Logging.RetentionDays, "logging.retention_days", warnings);
            options.Logging.RetentionDays = retention == 0 ? 1 : retention;
        }

        options.CodexExecutable = file.Codex?.Executable ?? options.CodexExecutable;
        options.ContinueAfterReset = file.Codex?.ContinueAfterReset ?? options.ContinueAfterReset;
        options.StartMinimized = file.Ui?.StartMinimized ?? options.StartMinimized;
        MapJobs(file.Jobs, options.Jobs, warnings);
        return options;
    }

    private static void MapJobs(JobsSection? jobs, JobsOptions o, List<string> warnings)
    {
        if (jobs is null)
        {
            return;
        }

        o.PrepareRemaining = Percent(jobs.PrepareRemaining, o.PrepareRemaining, "jobs.prepare_remaining", warnings);
        o.StopRemaining = Percent(jobs.StopRemaining, o.StopRemaining, "jobs.stop_remaining", warnings);
        if (o.StopRemaining > o.PrepareRemaining)
        {
            warnings.Add("jobs.stop_remaining is above jobs.prepare_remaining; using it for both.");
            o.PrepareRemaining = o.StopRemaining;
        }

        o.GraceSeconds = NonNegative(jobs.GraceSeconds, o.GraceSeconds, "jobs.grace_seconds", warnings);
        o.SavePatch = jobs.SavePatch ?? o.SavePatch;

        o.Codex.Model = jobs.Codex?.Model ?? o.Codex.Model;

        if (jobs.Codex?.Approvals is { } approvals)
        {
            if (approvals is "user" or "auto_review")
            {
                o.Codex.Approvals = approvals;
            }
            else
            {
                warnings.Add($"Unknown jobs.codex.approvals '{approvals}'; using 'user'.");
            }
        }

        if (jobs.Claude is { } claude)
        {
            o.Claude.Executable = claude.Executable ?? o.Claude.Executable;
            o.Claude.PermissionMode = string.IsNullOrWhiteSpace(claude.PermissionMode) ? o.Claude.PermissionMode : claude.PermissionMode;
            o.Claude.SuggestResetAfterMinutes = NonNegative(claude.SuggestResetAfterMinutes, o.Claude.SuggestResetAfterMinutes, "jobs.claude.suggest_reset_after_minutes", warnings);
            o.Claude.LimitResetsUrl = string.IsNullOrWhiteSpace(claude.LimitResetsUrl) ? o.Claude.LimitResetsUrl : claude.LimitResetsUrl;
        }
    }

    private static int Percent(int? value, int fallback, string key, List<string> warnings)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value is < 0 or > 100)
        {
            warnings.Add($"{key} must be between 0 and 100; using {fallback}.");
            return fallback;
        }

        return value.Value;
    }

    private static int NonNegative(int? value, int fallback, string key, List<string> warnings)
    {
        if (value is null)
        {
            return fallback;
        }

        if (value < 0)
        {
            warnings.Add($"{key} cannot be negative; using {fallback}.");
            return fallback;
        }

        return value.Value;
    }

    internal static readonly string DefaultToml = Render(new GuardOptions());
}

internal sealed class ConfigFile
{
    public string? Mode { get; set; }

    public MonitorSection? Monitor { get; set; }

    public LimitsSection? Limits { get; set; }

    public NotificationsSection? Notifications { get; set; }

    public ResetSection? Reset { get; set; }

    public AutomaticSection? Automatic { get; set; }

    public NearLimitSection? NearLimit { get; set; }

    public UpdatesSection? Updates { get; set; }

    public CodexSection? Codex { get; set; }

    public LoggingSection? Logging { get; set; }

    public UiSection? Ui { get; set; }

    public JobsSection? Jobs { get; set; }
}

internal sealed class LoggingSection
{
    public string? Level { get; set; }

    public int? RetentionDays { get; set; }
}

internal sealed class MonitorSection
{
    public bool? Enabled { get; set; }

    public int? IntervalSeconds { get; set; }
}

internal sealed class LimitsSection
{
    public bool? FiveHour { get; set; }

    public bool? Weekly { get; set; }
}

internal sealed class NotificationsSection
{
    public bool? Enabled { get; set; }
}

internal sealed class ResetSection
{
    public int? CooldownSeconds { get; set; }

    public int? VerifyAfterSeconds { get; set; }

    public int? VerifyIntervalSeconds { get; set; }

    public int? VerifyTimeoutSeconds { get; set; }

    public int? MinTimeToNaturalResetMinutes { get; set; }

    public int? ConsumeRetryMax { get; set; }
}

internal sealed class UpdatesSection
{
    public bool? CheckAutomatically { get; set; }
}

internal sealed class NearLimitSection
{
    public bool? Enabled { get; set; }

    public List<int>? Thresholds { get; set; }
}

internal sealed class AutomaticSection
{
    public int? MaxResetsPerDay { get; set; }

    public int? MaxResetsPerWeek { get; set; }
}

internal sealed class CodexSection
{
    public string? Executable { get; set; }

    public bool? ContinueAfterReset { get; set; }
}

internal sealed class JobsSection
{
    public int? PrepareRemaining { get; set; }

    public int? StopRemaining { get; set; }

    public int? GraceSeconds { get; set; }

    public bool? SavePatch { get; set; }

    public JobsCodexSection? Codex { get; set; }

    public JobsClaudeSection? Claude { get; set; }
}

internal sealed class JobsCodexSection
{
    public string? Approvals { get; set; }

    public string? Model { get; set; }
}

internal sealed class JobsClaudeSection
{
    public string? Executable { get; set; }

    public string? PermissionMode { get; set; }

    public int? SuggestResetAfterMinutes { get; set; }

    public string? LimitResetsUrl { get; set; }
}

internal sealed class UiSection
{
    public bool? StartMinimized { get; set; }
}

[TomlSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[TomlSerializable(typeof(ConfigFile))]
internal sealed partial class ConfigTomlContext : TomlSerializerContext
{
}
