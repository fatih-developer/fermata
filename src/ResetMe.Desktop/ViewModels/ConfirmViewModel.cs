using System.Globalization;
using ResetMe.Core.Monitoring;
using ResetMe.Core.Policies;

namespace ResetMe.Desktop.ViewModels;

/// <summary>Content of the reset confirmation dialog (PRD §38).</summary>
public sealed class ConfirmViewModel
{
    public ConfirmViewModel(LimitNotice notice, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var usage = notice.Usage;
        var assessment = notice.Assessment;

        Headline = NotificationTexts.ForLimit(notice, now).Title + ".";
        LiftsText = assessment.NaturalUnblockAt is { } at
            ? $"Lifts on its own in {NotificationTexts.Duration(at - now)}."
            : "";
        FiveHourLine = $"5-hour usage: {Percent(usage.FiveHour?.UsedPercent)}";
        WeeklyLine = $"Weekly usage: {Percent(usage.Weekly?.UsedPercent)}";
        CreditsLine = $"Available reset credits: {usage.AvailableResetCount.ToString(CultureInfo.InvariantCulture)}";

        var credit = assessment.SelectedCredit;
        CreditDetail = credit is null
            ? ""
            : $"Uses \"{credit.Title ?? "reset credit"}\""
              + (credit.ExpiresAt is { } e ? $", expires in {NotificationTexts.Duration(e - now)}" : "");

        Warning = assessment.NoOfferReason == NoOfferReason.NaturalResetSoon
            ? "The limit lifts on its own soon; using a credit now is probably not worth it."
            : usage.Weekly is { UsedPercent: < 50 } && !assessment.ExhaustedWindows.Contains(LimitWindowKind.Weekly)
                ? "Weekly usage is low, so part of the credit's value goes unused."
                : "";
    }

    public string Headline { get; }

    public string LiftsText { get; }

    public string FiveHourLine { get; }

    public string WeeklyLine { get; }

    public string CreditsLine { get; }

    public string CreditDetail { get; }

    public static string ScopeText => "A reset clears BOTH the 5-hour and the weekly window.";

    public string Warning { get; }

    public bool HasWarning => Warning.Length > 0;

    private static string Percent(double? value) =>
        value is null ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{value:0}%");
}
