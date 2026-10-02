using System.Globalization;
using ResetMe.Core.Policies;
using ResetMe.Core.Ports;
using ResetMe.Core.Reset;

namespace ResetMe.Core.Monitoring;

/// <summary>Notification wording, shared by every front end (daemon, watch, desktop).</summary>
public static class NotificationTexts
{
    public const string DesktopAskHint = "Open ResetMe to use one.";
    public const string TerminalAskHint = "Answer the prompt in your terminal.";
    public const string CommandAskHint = "Run `resetme reset` to use one.";

    /// <param name="askHint">Where the user answers the confirm-mode question (tray app, terminal, or nowhere).</param>
    public static Notification ForLimit(LimitNotice notice, DateTimeOffset now, string askHint = DesktopAskHint)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var usage = notice.Usage;
        var assessment = notice.Assessment;

        var which = assessment.ExhaustedWindows.Count switch
        {
            0 => "Codex usage is blocked",
            1 => assessment.ExhaustedWindows[0] == LimitWindowKind.FiveHour
                ? "5-hour Codex limit reached"
                : "Weekly Codex limit reached",
            _ => "5-hour and weekly Codex limits reached",
        };

        var lifts = assessment.NaturalUnblockAt is { } at ? $" Lifts on its own in {Duration(at - now)}." : "";
        var body = notice.Handling switch
        {
            LimitHandling.AskUser => $"{usage.AvailableResetCount} reset credit(s) available.{lifts} {askHint}",
            LimitHandling.ResetAutomatically => $"Using a reset credit automatically.{lifts}",
            LimitHandling.ReportOnly => $"{usage.AvailableResetCount} reset credit(s) available.{lifts} {CommandAskHint}",
            LimitHandling.AlreadyHandled => $"A reset was already attempted for this limit.{lifts}",
            _ => assessment.NoOfferReason switch
            {
                NoOfferReason.NoCredit => $"No reset credits left.{lifts}",
                NoOfferReason.NaturalResetSoon => $"Not worth a credit:{lifts}",
                NoOfferReason.WorkspaceLimit => "Workspace limit: reset credits do not apply.",
                _ => $"No reset offered.{lifts}",
            },
        };

        return new Notification(NotificationKind.LimitReached, which, body.Trim());
    }

    public static Notification ForReport(ResetReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Status switch
        {
            ResetRunStatus.Succeeded => new Notification(
                NotificationKind.ResetSucceeded,
                "Codex reset applied",
                $"Codex is usable again. Credits left: {report.UsageAfter?.AvailableResetCount.ToString(CultureInfo.InvariantCulture) ?? "?"}. Retry your Codex task."),
            ResetRunStatus.Unconfirmed => new Notification(
                NotificationKind.ResetProblem,
                "Codex reset unconfirmed",
                "The reset result could not be verified. No further credit will be used automatically."),
            ResetRunStatus.AutomaticCapReached => new Notification(
                NotificationKind.ResetProblem,
                "Automatic reset skipped",
                "Daily/weekly reset cap reached. Use ResetMe to reset manually."),
            ResetRunStatus.PendingAttemptNeedsUser => new Notification(
                NotificationKind.ResetProblem,
                "Reset needs attention",
                "An earlier reset attempt is unresolved. Run `resetme reset` to finish it."),
            ResetRunStatus.NoCredit => new Notification(NotificationKind.ResetProblem, "No reset credits", "No reset credits are available."),
            _ => new Notification(NotificationKind.Info, "Codex reset", report.Status.ToString()),
        };
    }

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
}
