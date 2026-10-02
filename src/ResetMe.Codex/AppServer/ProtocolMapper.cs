using System.Text.Json;
using System.Text.Json.Nodes;
using ResetMe.Core.Domain;

namespace ResetMe.Codex.AppServer;

public sealed class CodexProtocolException : Exception
{
    public CodexProtocolException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The single place that knows the app-server wire shapes (see docs/CODEX_INTEGRATION.md §4–5).
/// </summary>
internal static class ProtocolMapper
{
    public const string CodexLimitId = "codex";
    private const int FiveHourMinutes = 300;
    private const int WeeklyMinutes = 10080;

    public static CodexUsage MapUsage(JsonNode? result, DateTimeOffset readAt)
    {
        if (result is not JsonObject root)
        {
            throw new CodexProtocolException("account/rateLimits/read returned no result object.");
        }

        var snapshot = root["rateLimitsByLimitId"]?[CodexLimitId] as JsonObject
            ?? root["rateLimits"] as JsonObject;

        var (fiveHour, weekly) = MapWindows(snapshot);
        var credits = root["rateLimitResetCredits"] as JsonObject;

        return new CodexUsage
        {
            AccountId = GetString(root, "accountId"),
            UsageAllowed = GetBool(root, "ordinaryUsageAllowed"),
            ReachedType = snapshot is null ? null : GetString(snapshot, "rateLimitReachedType"),
            FiveHour = fiveHour,
            Weekly = weekly,
            AvailableResetCount = credits is null ? 0 : GetLong(credits, "availableCount") ?? 0,
            ResetCreditsReported = credits is not null,
            Credits = credits?["credits"] is JsonArray rows ? rows.Select(MapCredit).OfType<ResetCredit>().ToList() : null,
            ReadAt = readAt,
        };
    }

    public static ResetOutcome MapOutcome(JsonNode? result)
    {
        var outcome = result is JsonObject obj ? GetString(obj, "outcome") : null;
        return outcome switch
        {
            "reset" => ResetOutcome.Reset,
            "nothingToReset" => ResetOutcome.NothingToReset,
            "noCredit" => ResetOutcome.NoCredit,
            "alreadyRedeemed" => ResetOutcome.AlreadyRedeemed,
            _ => throw new CodexProtocolException($"Unknown consume outcome '{outcome ?? "<missing>"}'."),
        };
    }

    public static AccountStatus MapAccount(JsonNode? result)
    {
        // Deliberately never reads "email" (PII; PRD §28).
        if (result is not JsonObject root || root["account"] is not JsonObject account)
        {
            return new AccountStatus(false, null, null);
        }

        return new AccountStatus(true, GetString(account, "type"), GetString(account, "planType"));
    }

    /// <summary>
    /// Windows are matched by duration, never by primary/secondary position.
    /// </summary>
    private static (UsageWindow? FiveHour, UsageWindow? Weekly) MapWindows(JsonObject? snapshot)
    {
        if (snapshot is null)
        {
            return (null, null);
        }

        UsageWindow? fiveHour = null;
        UsageWindow? weekly = null;
        var unclassified = new List<UsageWindow>();

        foreach (var key in (ReadOnlySpan<string>)["primary", "secondary"])
        {
            if (snapshot[key] is not JsonObject node)
            {
                continue;
            }

            var window = new UsageWindow(
                GetDouble(node, "usedPercent") ?? 0,
                (int?)GetLong(node, "windowDurationMins"),
                FromUnix(GetLong(node, "resetsAt")));

            switch (window.WindowDurationMinutes)
            {
                case FiveHourMinutes:
                    fiveHour ??= window;
                    break;
                case WeeklyMinutes:
                    weekly ??= window;
                    break;
                default:
                    unclassified.Add(window);
                    break;
            }
        }

        // Unknown durations: shorter window counts as the 5-hour one.
        foreach (var window in unclassified.OrderBy(w => w.WindowDurationMinutes ?? int.MaxValue))
        {
            if (fiveHour is null && (window.WindowDurationMinutes ?? 0) < WeeklyMinutes)
            {
                fiveHour = window;
            }
            else
            {
                weekly ??= window;
            }
        }

        return (fiveHour, weekly);
    }

    private static ResetCredit? MapCredit(JsonNode? node)
    {
        if (node is not JsonObject obj || GetString(obj, "id") is not { } id)
        {
            return null;
        }

        var status = GetString(obj, "status") switch
        {
            "available" => ResetCreditStatus.Available,
            "redeeming" => ResetCreditStatus.Redeeming,
            "redeemed" => ResetCreditStatus.Redeemed,
            _ => ResetCreditStatus.Unknown,
        };

        return new ResetCredit(
            id,
            status,
            FromUnix(GetLong(obj, "grantedAt")) ?? DateTimeOffset.UnixEpoch,
            FromUnix(GetLong(obj, "expiresAt")),
            GetString(obj, "title"));
    }

    private static DateTimeOffset? FromUnix(long? seconds) =>
        seconds is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    private static string? GetString(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static bool? GetBool(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? v.GetValue<bool>()
            : null;

    private static double? GetDouble(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    private static long? GetLong(JsonObject obj, string name) =>
        GetDouble(obj, name) is { } d ? (long)d : null;
}
