using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodexResetGuard.Core.Domain;

namespace CodexResetGuard.Core.Policies;

public enum LimitWindowKind
{
    FiveHour,
    Weekly,
}

/// <summary>Why a blocked state does not lead to a reset offer.</summary>
public enum NoOfferReason
{
    WorkspaceLimit,
    NoCredit,
    WindowDisabled,
    NaturalResetSoon,
}

public sealed record LimitAssessment(
    MonitorState State,
    bool Blocked,
    IReadOnlyList<LimitWindowKind> ExhaustedWindows,
    DateTimeOffset? NaturalUnblockAt,
    ResetCredit? SelectedCredit,
    NoOfferReason? NoOfferReason,
    string? LimitEventId)
{
    public bool ResetOffered => State == MonitorState.ResetAvailable;
}

/// <summary>Implements the trigger and offer rules of PRD §8.</summary>
public static class LimitEvaluator
{
    public static LimitAssessment Assess(
        CodexUsage usage,
        GuardOptions options,
        DateTimeOffset now,
        bool ignoreNaturalResetThreshold = false)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(options);

        var exhausted = ExhaustedWindows(usage);
        var blocked = IsBlocked(usage);

        if (!blocked)
        {
            return new LimitAssessment(MonitorState.Healthy, false, exhausted, null, null, null, null);
        }

        var unblockAt = NaturalUnblockAt(usage, exhausted);
        var eventId = ComputeLimitEventId(usage, exhausted);
        var credit = SelectCredit(usage.Credits);

        NoOfferReason? reason = null;
        if (usage.IsWorkspaceLimit)
        {
            reason = NoOfferReason.WorkspaceLimit;
        }
        else if (usage.AvailableResetCount <= 0)
        {
            reason = NoOfferReason.NoCredit;
        }
        else if (exhausted.Count > 0 && !exhausted.Any(w => IsEnabled(w, options.Limits)))
        {
            reason = NoOfferReason.WindowDisabled;
        }
        else if (!ignoreNaturalResetThreshold
            && unblockAt is { } at
            && at - now < TimeSpan.FromMinutes(options.Reset.MinTimeToNaturalResetMinutes))
        {
            reason = NoOfferReason.NaturalResetSoon;
        }

        var state = reason is null ? MonitorState.ResetAvailable : MonitorState.LimitReached;
        return new LimitAssessment(state, true, exhausted, unblockAt, credit, reason, eventId);
    }

    /// <summary>
    /// PRD §8.1: the backend flag is authoritative; percentages only count when it is unknown.
    /// </summary>
    public static bool IsBlocked(CodexUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return usage.UsageAllowed switch
        {
            false => true,
            true => false,
            null => (usage.FiveHour?.IsExhausted ?? false) || (usage.Weekly?.IsExhausted ?? false),
        };
    }

    /// <summary>PRD FR-06 success criterion.</summary>
    public static bool IsUsable(CodexUsage usage) => !IsBlocked(usage);

    public static IReadOnlyList<LimitWindowKind> ExhaustedWindows(CodexUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var list = new List<LimitWindowKind>(2);
        if (usage.FiveHour?.IsExhausted == true)
        {
            list.Add(LimitWindowKind.FiveHour);
        }

        if (usage.Weekly?.IsExhausted == true)
        {
            list.Add(LimitWindowKind.Weekly);
        }

        return list;
    }

    /// <summary>
    /// The moment every exhausted window has reopened on its own, or null when unknown.
    /// </summary>
    public static DateTimeOffset? NaturalUnblockAt(CodexUsage usage, IReadOnlyList<LimitWindowKind> exhausted)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(exhausted);
        if (exhausted.Count == 0)
        {
            return null;
        }

        DateTimeOffset? latest = null;
        foreach (var kind in exhausted)
        {
            var resetsAt = Window(usage, kind)?.ResetsAt;
            if (resetsAt is null)
            {
                return null;
            }

            if (latest is null || resetsAt > latest)
            {
                latest = resetsAt;
            }
        }

        return latest;
    }

    /// <summary>
    /// PRD §8.3: redeem the available credit that expires first. Null lets the backend choose.
    /// </summary>
    public static ResetCredit? SelectCredit(IReadOnlyList<ResetCredit>? credits) =>
        credits?
            .Where(c => c.Status == ResetCreditStatus.Available)
            .OrderBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue)
            .ThenBy(c => c.GrantedAt)
            .FirstOrDefault();

    /// <summary>
    /// PRD §19.2: identifies one blocking episode, so a single episode never triggers two attempts.
    /// A new episode starts when the blocking windows roll over to new reset times.
    /// </summary>
    public static string ComputeLimitEventId(CodexUsage usage, IReadOnlyList<LimitWindowKind> exhausted)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(exhausted);

        // Blocked by the backend flag with no exhausted window: key on every known window.
        IEnumerable<LimitWindowKind> kinds = exhausted.Count > 0
            ? exhausted
            : [LimitWindowKind.FiveHour, LimitWindowKind.Weekly];

        var builder = new StringBuilder(usage.AccountId ?? "unknown-account");
        foreach (var kind in kinds.Order())
        {
            var resetsAt = Window(usage, kind)?.ResetsAt?.ToUnixTimeSeconds();
            builder.Append('|').Append(kind).Append(':')
                .Append(resetsAt?.ToString(CultureInfo.InvariantCulture) ?? "?");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private static UsageWindow? Window(CodexUsage usage, LimitWindowKind kind) =>
        kind == LimitWindowKind.FiveHour ? usage.FiveHour : usage.Weekly;

    private static bool IsEnabled(LimitWindowKind kind, LimitOptions limits) =>
        kind == LimitWindowKind.FiveHour ? limits.FiveHour : limits.Weekly;
}
