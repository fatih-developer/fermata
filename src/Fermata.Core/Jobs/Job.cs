namespace Fermata.Core.Jobs;

public enum JobProviderKind
{
    Codex,
    Claude,
}

/// <summary>Fermata started the session, or took over one the user already had.</summary>
public enum JobOrigin
{
    Started,
    Adopted,
}

/// <summary>
/// Lifecycle of a supervised job. Short transitions (resume pending, resuming) are not states;
/// they are recorded as events.
/// </summary>
public enum JobStatus
{
    /// <summary>Waits for its start time (<c>run --at/--in</c>).</summary>
    Scheduled,
    Running,

    /// <summary>Asked to stop at the next turn boundary; the checkpoint follows.</summary>
    Checkpointing,
    WaitingQuota,

    /// <summary>Stopped by the user (or by a session that ended outside Fermata).</summary>
    Paused,
    BlockedApproval,
    BlockedUser,

    /// <summary>The repository moved on since the checkpoint; resume needs <c>--force</c>.</summary>
    BlockedWorkspace,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>When a stopped job continues.</summary>
public enum ResumeMode
{
    /// <summary>Only when the user says so.</summary>
    Manual,

    /// <summary>As soon as the provider's quota allows work again.</summary>
    QuotaAvailable,

    /// <summary>At <see cref="Job.ResumeAt"/> (and only if the quota allows it then).</summary>
    At,
}

/// <summary>Why the job is being stopped at a turn boundary.</summary>
public enum StopReason
{
    Quota,
    User,
}

/// <summary>Provider-side identity of the session the job drives.</summary>
/// <param name="Id">Codex thread id or Claude session id.</param>
/// <param name="ShortId">Claude background job id (<c>claude --bg</c>), when there is one.</param>
public sealed record SessionRef(string Id, string? ShortId = null);

/// <summary>Where the workspace stood when the job last stopped.</summary>
public sealed record CheckpointInfo(
    DateTimeOffset At,
    string Path,
    string? Branch,
    string? Head,
    bool Dirty);

public sealed record Job
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public required JobProviderKind Provider { get; init; }

    public JobOrigin Origin { get; init; } = JobOrigin.Started;

    public required string Cwd { get; init; }

    public SessionRef? Session { get; init; }

    public required string Objective { get; init; }

    public JobStatus Status { get; init; } = JobStatus.Scheduled;

    public ResumeMode ResumeMode { get; init; } = ResumeMode.QuotaAvailable;

    /// <summary>Start time while Scheduled; earliest resume time while waiting or paused.</summary>
    public DateTimeOffset? ResumeAt { get; init; }

    /// <summary>Human-readable reason for a Blocked*, Paused or Failed status.</summary>
    public string? BlockReason { get; init; }

    public StopReason? StopReason { get; init; }

    public CheckpointInfo? LastCheckpoint { get; init; }

    public QuotaSnapshot? LastQuota { get; init; }

    /// <summary>When the agent was asked to write <c>.fermata/handoff.md</c> (once per quota episode).</summary>
    public DateTimeOffset? HandoffRequestedAt { get; init; }

    /// <summary>When the user was told about a manual reset credit (once per quota episode).</summary>
    public DateTimeOffset? CreditSuggestedAt { get; init; }

    /// <summary>Resume at the next tick even if the last known quota says the limit is on (<c>fermata resume --now</c>).</summary>
    public bool ResumeNow { get; init; }

    /// <summary>Skip the workspace check on the next resume (<c>fermata resume --force</c>).</summary>
    public bool ForceResume { get; init; }

    /// <summary>Codex: who answers approvals ("user" or "auto_review"). Claude: the permission mode.</summary>
    public string? Approvals { get; init; }

    public int FailureCount { get; init; }

    /// <summary>Backoff after a failed provider call: nothing is tried before this.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    public string? LastError { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public string DisplayName => Name ?? Id;

    public bool IsFinished => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary>A Fermata-side session exists (a scheduled job has none yet).</summary>
    public bool HasSession => Session is not null;
}

/// <summary>One line of <c>events.ndjson</c>.</summary>
public sealed record JobEvent(DateTimeOffset At, string Kind, string Message);

/// <summary>Turns objectives and names into job ids: lowercase ASCII words joined by dashes.</summary>
public static class JobIds
{
    public const int MaxLength = 40;

    public static string Slug(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new System.Text.StringBuilder();
        var dash = false;
        foreach (var raw in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            var c = char.ToLowerInvariant(raw);
            var ascii = c switch
            {
                'ı' => "i", // dotless i (Turkish) has no decomposition
                'ł' => "l",
                'ø' => "o",
                'đ' => "d",
                'ß' => "ss",
                'æ' => "ae",
                >= 'a' and <= 'z' or >= '0' and <= '9' => c.ToString(),
                _ => null,
            };
            if (ascii is not null)
            {
                if (dash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(ascii);
                dash = false;
            }
            else if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(raw) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                dash = true;
            }

            if (builder.Length >= MaxLength)
            {
                break;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "job" : slug;
    }

    /// <summary>The slug, or the slug with -2, -3â¦ when <paramref name="exists"/> says it is taken.</summary>
    public static string Unique(string text, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var slug = Slug(text);
        if (!exists(slug))
        {
            return slug;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{slug}-{i}";
            if (!exists(candidate))
            {
                return candidate;
            }
        }
    }
}
