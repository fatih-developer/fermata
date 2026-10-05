using System.Text.Json.Nodes;
using Fermata.Cli.Output;
using Fermata.Core.Domain;
using Fermata.Core.Policies;
using Fermata.Core.Reset;

namespace Fermata.Cli.Commands;

internal static class StatusCommand
{
    public static async Task<int> RunAsync(bool json, CancellationToken cancellationToken)
    {
        var runtime = GuardRuntime.Load();
        await using var client = await runtime.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var account = await client.GetAccountAsync(cancellationToken).ConfigureAwait(false);
        if (!account.IsAuthenticated)
        {
            Console.Error.WriteLine("AUTH_REQUIRED: Codex is not logged in. Run `codex login` first.");
            return ExitCodes.AuthRequired;
        }

        var usage = await client.GetUsageAsync(includeCreditDetails: true, cancellationToken).ConfigureAwait(false);
        var now = TimeProvider.System.GetUtcNow();
        var assessment = LimitEvaluator.Assess(usage, runtime.Options, now);
        var state = runtime.StateStore.Load();

        if (json)
        {
            Console.WriteLine(ToJson(usage, assessment, state, runtime.Options, now).ToJsonString(JsonOptions.Indented));
        }
        else
        {
            Print(usage, assessment, state, runtime.Options, now);
        }

        return ExitCodes.Ok;
    }

    private static void Print(CodexUsage usage, LimitAssessment assessment, ResetState state, GuardOptions options, DateTimeOffset now)
    {
        var credits = usage.ResetCreditsReported ? usage.AvailableResetCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "n/a";

        Console.WriteLine("Codex Usage");
        Console.WriteLine(Format.Rule);
        Console.WriteLine($"5 hour     {Format.Percent(usage.FiveHour)}   {Format.ResetsIn(usage.FiveHour, now)}");
        Console.WriteLine($"Weekly     {Format.Percent(usage.Weekly)}   {Format.ResetsIn(usage.Weekly, now)}");
        Console.WriteLine($"Resets     {credits,4}    {Format.NextCreditExpiry(usage, now)}");
        Console.WriteLine($"Mode       {Format.Mode(options.Mode)}");
        Console.WriteLine();
        Console.WriteLine($"Status     {Format.StatusCode(assessment, usage)}");

        var action = Format.ActionCode(assessment);
        if (action.Length > 0)
        {
            Console.WriteLine($"Action     {action}");
        }

        if (assessment is { NoOfferReason: NoOfferReason.NaturalResetSoon, NaturalUnblockAt: { } at })
        {
            Console.WriteLine($"           limit lifts on its own in {Format.Duration(at - now)}");
        }

        if (state.Pending is { } pending)
        {
            Console.WriteLine();
            Console.WriteLine($"Pending    unresolved reset attempt from {pending.StartedAt.ToLocalTime():g}");
            Console.WriteLine("           run `fermata reset` to finish it (same idempotency key)");
        }
    }

    private static JsonObject ToJson(CodexUsage usage, LimitAssessment assessment, ResetState state, GuardOptions options, DateTimeOffset now)
    {
        static JsonObject? Window(UsageWindow? w) => w is null
            ? null
            : new JsonObject
            {
                ["usedPercent"] = w.UsedPercent,
                ["resetsAt"] = w.ResetsAt?.ToString("O"),
            };

        return new JsonObject
        {
            ["status"] = Format.StatusCode(assessment, usage),
            ["state"] = assessment.State.ToString(),
            ["action"] = Format.ActionCode(assessment) is { Length: > 0 } a ? a : null,
            ["usageAllowed"] = usage.UsageAllowed,
            ["fiveHour"] = Window(usage.FiveHour),
            ["weekly"] = Window(usage.Weekly),
            ["resetCredits"] = usage.ResetCreditsReported ? usage.AvailableResetCount : null,
            ["nextCreditExpiresAt"] = LimitEvaluator.SelectCredit(usage.Credits)?.ExpiresAt?.ToString("O"),
            ["naturalUnblockAt"] = assessment.NaturalUnblockAt?.ToString("O"),
            ["mode"] = Format.Mode(options.Mode),
            ["pendingAttempt"] = state.Pending is not null,
            ["readAt"] = now.ToString("O"),
        };
    }
}

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int AuthRequired = 2;
    public const int CodexUnavailable = 3;
    public const int Declined = 4;
}

internal static class JsonOptions
{
    public static readonly System.Text.Json.JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
