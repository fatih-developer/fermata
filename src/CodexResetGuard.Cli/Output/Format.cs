using System.Globalization;
using CodexResetGuard.Core.Domain;
using CodexResetGuard.Core.Policies;

namespace CodexResetGuard.Cli.Output;

internal static class Format
{
    public const string Rule = "──────────────────────────────────────";

    public static string Percent(UsageWindow? window) =>
        window is null ? "  n/a" : string.Create(CultureInfo.InvariantCulture, $"{window.UsedPercent,4:0}%");

    /// <summary>"2h 40m", "3d 4h", "8m", "<1m".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return "now";
        }

        if (span.TotalDays >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays}d {span.Hours}h");
        }

        if (span.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes}m");
        }

        return span.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m")
            : "<1m";
    }

    public static string ResetsIn(UsageWindow? window, DateTimeOffset now) =>
        window?.ResetsAt is { } at ? $"resets in {Duration(at - now)}" : "";

    public static string WindowName(LimitWindowKind kind) =>
        kind == LimitWindowKind.FiveHour ? "5-hour" : "weekly";

    public static string StatusCode(LimitAssessment assessment, CodexUsage usage) =>
        assessment.Blocked
            ? "LIMIT_REACHED"
            : usage.UsageAllowed is null ? "USAGE_UNKNOWN" : "OK";

    public static string ActionCode(LimitAssessment assessment) => assessment.NoOfferReason switch
    {
        null when assessment.ResetOffered => "RESET_AVAILABLE",
        NoOfferReason.NoCredit => "NO_RESET_CREDIT",
        NoOfferReason.WorkspaceLimit => "WORKSPACE_LIMIT",
        NoOfferReason.NaturalResetSoon => "WAIT_NATURAL_RESET",
        NoOfferReason.WindowDisabled => "WINDOW_DISABLED",
        _ => "",
    };

    public static string Mode(GuardMode mode) => mode.ToString().ToLowerInvariant();

    public static string? NextCreditExpiry(CodexUsage usage, DateTimeOffset now) =>
        LimitEvaluator.SelectCredit(usage.Credits)?.ExpiresAt is { } expires
            ? $"next expires in {Duration(expires - now)}"
            : null;
}
