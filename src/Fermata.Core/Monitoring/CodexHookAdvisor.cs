using System.Globalization;
using Fermata.Core.Policies;

namespace Fermata.Core.Monitoring;

/// <summary>
/// What Fermata tells the user inside Codex (hook <c>systemMessage</c>). Only reads the snapshot, so
/// hooks stay instant. Never blocks a prompt: a stale snapshot must not stop work Codex would allow.
/// </summary>
public static class CodexHookAdvisor
{
    /// <summary>Prompts get a warning from this usage on; below it they stay silent.</summary>
    public const double PromptWarningPercent = 95;

    /// <summary>A new session gets a summary from this usage on.</summary>
    public const double SessionWarningPercent = 80;

    public const string NotMonitoring = "Fermata is not monitoring Codex limits right now. Start the Fermata app (or `fermata daemon`) to get limit protection.";

    public static string? ForSessionStart(StatusSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null || !snapshot.IsFresh(now))
        {
            return NotMonitoring;
        }

        if (snapshot.Blocked)
        {
            return LimitMessage(snapshot, now);
        }

        return snapshot.PeakPercent >= SessionWarningPercent ? UsageMessage(snapshot, now) : null;
    }

    public static string? ForPrompt(StatusSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null || !snapshot.IsFresh(now))
        {
            return null; // Said once at session start; repeating it on every prompt is noise.
        }

        if (snapshot.Blocked)
        {
            return LimitMessage(snapshot, now);
        }

        return snapshot.PeakPercent >= PromptWarningPercent ? UsageMessage(snapshot, now) : null;
    }

    /// <summary>Full status for the MCP tool: usage or limit, plus the last reset event.</summary>
    public static string Describe(StatusSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null || !snapshot.IsFresh(now))
        {
            return NotMonitoring;
        }

        var text = snapshot.Blocked ? LimitMessage(snapshot, now) : UsageMessage(snapshot, now);
        if (snapshot.LastEvent is { } last && snapshot.LastEventAt is { } at)
        {
            text += $" Last Fermata event ({NotificationTexts.Duration(now - at)} ago): {last}";
        }

        return text;
    }

    /// <summary>"Codex usage: 5-hour 92% (resets in 1h 10m), weekly 41%. 2 reset credits; auto-reset on."</summary>
    public static string UsageMessage(StatusSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return $"Codex usage: 5-hour {Window(snapshot.FiveHourPercent, snapshot.FiveHourResetsAt, now)}, "
            + $"weekly {Window(snapshot.WeeklyPercent, snapshot.WeeklyResetsAt, now)}. "
            + $"{Credits(snapshot.ResetCredits)}; auto-reset {(snapshot.Mode == GuardMode.Automatic ? "on" : "off")}.";
    }

    public static string LimitMessage(StatusSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var which = snapshot.ExhaustedWindows.Count switch
        {
            0 => "Codex usage is blocked",
            1 => snapshot.ExhaustedWindows[0] == LimitWindowKind.FiveHour ? "Codex 5-hour limit reached" : "Codex weekly limit reached",
            _ => "Codex 5-hour and weekly limits reached",
        };
        var lifts = snapshot.NaturalUnblockAt is { } at ? $" Lifts on its own in {NotificationTexts.Duration(at - now)}." : "";

        var next = snapshot.NoOfferReason switch
        {
            NoOfferReason.NoCredit => "No reset credits left.",
            NoOfferReason.WorkspaceLimit => "This is a workspace limit; reset credits do not apply.",
            NoOfferReason.NaturalResetSoon => $"{Credits(snapshot.ResetCredits)}; not using one because it lifts soon.",
            NoOfferReason.WindowDisabled => $"{Credits(snapshot.ResetCredits)}; resets for this window are disabled in Fermata.",
            _ => snapshot.Mode switch
            {
                GuardMode.Automatic => $"Auto-reset is on: Fermata is using one of {Credits(snapshot.ResetCredits)}.",
                GuardMode.Confirm => $"{Credits(snapshot.ResetCredits)} available: use one from the Fermata tray icon (Reset now…) or run `fermata reset`.",
                _ => $"{Credits(snapshot.ResetCredits)} available: run `fermata reset` to use one.",
            },
        };

        return $"{which}.{lifts} {next}";
    }

    private static string Credits(long count) =>
        count == 1 ? "1 reset credit" : string.Create(CultureInfo.InvariantCulture, $"{count} reset credits");

    private static string Window(double? percent, DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (percent is not { } p)
        {
            return "n/a";
        }

        var text = string.Create(CultureInfo.InvariantCulture, $"{p:0}%");
        return resetsAt is { } at ? $"{text} (resets in {NotificationTexts.Duration(at - now)})" : text;
    }
}
