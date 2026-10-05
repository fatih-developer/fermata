using System.Globalization;
using Fermata.Cli.Commands;
using Fermata.Core.Domain;
using Fermata.Core.Policies;
using Fermata.Core.Reset;

namespace Fermata.Cli.Output;

/// <summary>Reset offer, refusal and result texts shared by `reset` and `watch`.</summary>
internal static class ResetPresenter
{
    public static void PrintOffer(CodexUsage usage, LimitAssessment assessment, DateTimeOffset now)
    {
        var windows = assessment.ExhaustedWindows;
        var headline = windows.Count switch
        {
            0 => $"Codex usage is blocked ({usage.ReachedType ?? "reason not reported"}).",
            1 => $"{Capitalize(Format.WindowName(windows[0]))} Codex limit reached",
            _ => "5-hour and weekly Codex limits reached",
        };

        if (windows.Count > 0 && assessment.NaturalUnblockAt is { } at)
        {
            headline += $" (lifts on its own in {Format.Duration(at - now)}).";
        }
        else if (windows.Count > 0)
        {
            headline += ".";
        }

        Console.WriteLine(headline);
        Console.WriteLine($"5-hour usage: {Format.Percent(usage.FiveHour).Trim()}   Weekly usage: {Format.Percent(usage.Weekly).Trim()}");

        var credit = assessment.SelectedCredit;
        var expiry = credit?.ExpiresAt is { } e ? $", the one used expires in {Format.Duration(e - now)}" : "";
        Console.WriteLine($"Reset credits: {usage.AvailableResetCount.ToString(CultureInfo.InvariantCulture)}{expiry}");
        Console.WriteLine("A reset clears BOTH the 5-hour and the weekly window.");
        if (usage.Weekly is { UsedPercent: < 50 } && !windows.Contains(LimitWindowKind.Weekly))
        {
            Console.WriteLine("Note: weekly usage is low, so part of the credit's value goes unused.");
        }

        Console.WriteLine();
    }

    public static string Explain(NoOfferReason reason, CodexUsage usage, LimitAssessment assessment, GuardOptions options, DateTimeOffset now)
    {
        var lifts = assessment.NaturalUnblockAt is { } at ? $" The limit lifts on its own in {Format.Duration(at - now)}." : "";
        return reason switch
        {
            NoOfferReason.NoCredit => "NO_RESET_CREDIT: no reset credits available." + lifts,
            NoOfferReason.WorkspaceLimit =>
                $"WORKSPACE_LIMIT: this block comes from your workspace ({usage.ReachedType}). Reset credits do not apply; contact the workspace owner.",
            NoOfferReason.WindowDisabled => "The blocking window is disabled under [limits] in config.toml." + lifts,
            NoOfferReason.NaturalResetSoon =>
                $"Not worth a credit:{lifts} (threshold: {options.Reset.MinTimeToNaturalResetMinutes} min). Use --force to reset anyway.",
            _ => reason.ToString(),
        };
    }

    public static int PrintReport(ResetReport report, GuardOptions options, DateTimeOffset now)
    {
        switch (report.Status)
        {
            case ResetRunStatus.Succeeded:
                var after = report.UsageAfter;
                Console.WriteLine("Reset applied and verified. Codex is usable again.");
                if (after is not null)
                {
                    Console.WriteLine($"5-hour {Format.Percent(after.FiveHour).Trim()}   weekly {Format.Percent(after.Weekly).Trim()}   credits left {after.AvailableResetCount.ToString(CultureInfo.InvariantCulture)}");
                }

                Console.WriteLine("Retry or continue your Codex task; it does not resume by itself.");
                return ExitCodes.Ok;

            case ResetRunStatus.Unconfirmed:
                Console.WriteLine($"RESET_UNCONFIRMED: the result could not be verified (backend outcome: {report.Outcome?.ToString() ?? "unknown"}).");
                Console.WriteLine("No further credit will be used automatically. Check `fermata status` in a minute.");
                return ExitCodes.Error;

            case ResetRunStatus.NothingToReset:
                Console.WriteLine("The backend reported nothing to reset.");
                return ExitCodes.Ok;

            case ResetRunStatus.NoCredit:
                Console.WriteLine("NO_RESET_CREDIT: no reset credits available.");
                return ExitCodes.Error;

            case ResetRunStatus.NotBlocked:
                Console.WriteLine("Codex is no longer rate-limited; nothing to reset. No credit was used.");
                return ExitCodes.Ok;

            case ResetRunStatus.NotOffered when report.NoOfferReason is { } reason && report.UsageBefore is { } before && report.Assessment is { } assessment:
                Console.WriteLine(Explain(reason, before, assessment, options, now));
                return ExitCodes.Error;

            case ResetRunStatus.AlreadyHandled:
                Console.WriteLine("A reset was already attempted for this limit episode. Use --force to try again.");
                return ExitCodes.Error;

            case ResetRunStatus.CooldownActive:
                Console.WriteLine($"A reset was attempted less than {options.Reset.CooldownSeconds}s ago. Wait, or use --force.");
                return ExitCodes.Error;

            case ResetRunStatus.LockBusy:
                Console.WriteLine("Another Fermata process is running a reset. Try again shortly.");
                return ExitCodes.Error;

            case ResetRunStatus.AutomaticCapReached:
                Console.WriteLine($"Automatic mode: reset cap reached ({options.Automatic.MaxResetsPerDay}/day, {options.Automatic.MaxResetsPerWeek}/week). Run `fermata reset` to reset manually.");
                return ExitCodes.Error;

            case ResetRunStatus.PendingAttemptNeedsUser:
                Console.WriteLine("An earlier reset attempt has no confirmed result. Run `fermata reset` to finish it with the same idempotency key.");
                return ExitCodes.Error;

            default:
                Console.WriteLine(report.Status.ToString());
                return ExitCodes.Error;
        }
    }

    public static bool Confirm(bool yes, string prompt)
    {
        if (yes)
        {
            return true;
        }

        if (Console.IsInputRedirected)
        {
            Console.WriteLine("stdin is not interactive; pass --yes to confirm.");
            return false;
        }

        Console.Write(prompt);
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return answer is "y" or "yes" or "e" or "evet";
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
