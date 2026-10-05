namespace Fermata.Core.Jobs;

public enum JobActionKind
{
    None,

    /// <summary>Start the session of a scheduled job.</summary>
    Start,

    /// <summary>Continue the session (workspace already checked).</summary>
    Resume,

    /// <summary>Ask the agent for the handoff note before the quota runs out.</summary>
    RequestHandoff,

    /// <summary>Let the running turn finish, then stop (status Checkpointing).</summary>
    PauseAtTurnBoundary,

    /// <summary>Record branch/HEAD/status, then move to <see cref="JobAction.Status"/>.</summary>
    WriteCheckpoint,

    /// <summary>Wait for the quota until <see cref="JobAction.Until"/> (status WaitingQuota).</summary>
    WaitUntil,

    /// <summary>Set <see cref="JobAction.Status"/> without touching the session.</summary>
    SetStatus,

    /// <summary>Tell the user a reset credit on the provider's web page would end the wait.</summary>
    SuggestCredit,
    Complete,
}

/// <param name="Status">Status after the action (WriteCheckpoint, SetStatus, PauseAtTurnBoundary, WaitUntil).</param>
/// <param name="Until">New ResumeAt (WaitUntil, WriteCheckpoint into a wait).</param>
/// <param name="Reason">Shown to the user; becomes the job's BlockReason where it applies.</param>
public sealed record JobAction(
    JobActionKind Kind,
    JobStatus? Status = null,
    DateTimeOffset? Until = null,
    string? Reason = null,
    StopReason? StopReason = null)
{
    public static readonly JobAction None = new(JobActionKind.None);

    public static JobAction Set(JobStatus status, string? reason = null) => new(JobActionKind.SetStatus, status, Reason: reason);
}

public sealed record ProviderCapabilities(ResetCreditSupport ResetCredits, bool SupportsHandoff)
{
    public static ProviderCapabilities Of(IJobProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new(provider.ResetCredits, provider.SupportsHandoff);
    }
}

/// <summary>Everything <see cref="JobPolicy.Decide"/> looks at besides the job.</summary>
/// <param name="QuotaConfirmed">The provider itself says work is allowed now (Codex usageAllowed=true), e.g. after a credit.</param>
/// <param name="WorkspaceChanged">Branch or HEAD differ from the last checkpoint.</param>
public sealed record JobContext(
    QuotaAssessment Quota,
    bool QuotaConfirmed,
    SessionObservation Session,
    bool WorkspaceChanged,
    ProviderCapabilities Provider,
    DateTimeOffset Now,
    TimeSpan SuggestCreditAfter);

/// <summary>
/// The job state machine as one pure function: (job, context) → next action. It never races the
/// provider's own recovery: while a goal keeps going or Claude's auto-resume is armed, it waits.
/// </summary>
public static class JobPolicy
{
    public static JobAction Decide(Job job, JobContext context)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        if (job.IsFinished || job.NextAttemptAt > context.Now)
        {
            return JobAction.None;
        }

        return job.Status switch
        {
            JobStatus.Scheduled => job.ResumeAt > context.Now ? JobAction.None : TryContinue(job, context),
            JobStatus.Paused => job.ResumeNow ? TryContinue(job, context) : DecidePaused(job, context),
            JobStatus.WaitingQuota => DecideWaiting(job, context),
            JobStatus.Checkpointing => DecideCheckpointing(job, context),
            JobStatus.Running or JobStatus.BlockedApproval or JobStatus.BlockedUser => DecideActive(job, context),
            _ => JobAction.None, // BlockedWorkspace: only `fermata resume --force` moves it.
        };
    }

    private static JobAction DecidePaused(Job job, JobContext context)
    {
        if (context.Session.State == SessionState.Completed)
        {
            return new JobAction(JobActionKind.Complete);
        }

        return job.ResumeMode switch
        {
            ResumeMode.Manual => JobAction.None,
            ResumeMode.At when job.ResumeAt > context.Now => JobAction.None,
            _ => TryContinue(job, context),
        };
    }

    private static JobAction DecideWaiting(Job job, JobContext context)
    {
        switch (context.Session.State)
        {
            case SessionState.NativeWaiting:
                return SuggestCredit(job, context) ?? JobAction.None;
            case SessionState.Running:
                return JobAction.Set(JobStatus.Running, "The session continued on its own.");
            case SessionState.Completed:
                return new JobAction(JobActionKind.Complete);
        }

        if (job.ResumeNow)
        {
            return TryContinue(job, context);
        }

        if (job.ResumeMode == ResumeMode.At && job.ResumeAt > context.Now)
        {
            return SuggestCredit(job, context) ?? JobAction.None;
        }

        // Without the provider's word, the reset time is all there is (Claude cannot re-check its quota).
        if (job.ResumeMode == ResumeMode.QuotaAvailable && !context.QuotaConfirmed && job.ResumeAt > context.Now)
        {
            return context.Quota.Blocked ? SuggestCredit(job, context) ?? Wait(job, context) : JobAction.None;
        }

        return TryContinue(job, context);
    }

    private static JobAction DecideCheckpointing(Job job, JobContext context)
    {
        var state = context.Session.State;
        switch (state)
        {
            case SessionState.Running or SessionState.Unknown:
                return JobAction.None;
            case SessionState.Completed:
                return new JobAction(JobActionKind.Complete);
            case SessionState.WaitingApproval:
                return JobAction.Set(JobStatus.BlockedApproval, "Waiting for an approval.");
            case SessionState.WaitingInput:
                return JobAction.Set(JobStatus.BlockedUser, "Waiting for your answer.");
        }

        // The turn is over (idle, paused, stopped at the limit, natively waiting or gone).
        if (job.StopReason == StopReason.User)
        {
            return new JobAction(JobActionKind.WriteCheckpoint, JobStatus.Paused, job.ResumeAt, "Paused.", StopReason.User);
        }

        return new JobAction(JobActionKind.WriteCheckpoint, JobStatus.WaitingQuota, context.Quota.ResumeAt ?? job.ResumeAt, WaitReason(context), StopReason.Quota);
    }

    private static JobAction DecideActive(Job job, JobContext context)
    {
        switch (context.Session.State)
        {
            case SessionState.Unknown:
                return JobAction.None;
            case SessionState.Completed:
                return new JobAction(JobActionKind.Complete);
            case SessionState.WaitingApproval:
                return job.Status == JobStatus.BlockedApproval ? JobAction.None : JobAction.Set(JobStatus.BlockedApproval, "Waiting for an approval.");
            case SessionState.WaitingInput:
                return job.Status == JobStatus.BlockedUser ? JobAction.None : JobAction.Set(JobStatus.BlockedUser, "Waiting for your answer.");
            case SessionState.NativeWaiting:
                return new JobAction(JobActionKind.WaitUntil, JobStatus.WaitingQuota, context.Quota.ResumeAt, "Stopped at the usage limit; the agent continues on its own after the reset.");
            case SessionState.LimitStopped:
                return new JobAction(JobActionKind.WriteCheckpoint, JobStatus.WaitingQuota, context.Quota.ResumeAt, WaitReason(context), StopReason.Quota);
            case SessionState.Interrupted:
                return TryContinue(job, context);
            case SessionState.Paused:
                return new JobAction(JobActionKind.WriteCheckpoint, JobStatus.Paused, null, "Paused outside Fermata.", StopReason.User);
            case SessionState.Gone:
                return context.Quota.Blocked
                    ? new JobAction(JobActionKind.WriteCheckpoint, JobStatus.WaitingQuota, context.Quota.ResumeAt, WaitReason(context), StopReason.Quota)
                    : new JobAction(JobActionKind.WriteCheckpoint, JobStatus.Paused, null, "The session ended outside Fermata. Run `fermata resume` to continue it.", StopReason.User);
        }

        if (job.Status != JobStatus.Running)
        {
            return JobAction.Set(JobStatus.Running, "Continuing.");
        }

        if (context.Session.State != SessionState.Running)
        {
            return JobAction.None; // Idle between turns: the goal (or the user) drives the next one.
        }

        if (context.Quota.AtLeast(QuotaLevel.StopNewWork))
        {
            return new JobAction(JobActionKind.PauseAtTurnBoundary, JobStatus.Checkpointing, context.Quota.ResumeAt, "Almost out of quota: stopping after this turn.", StopReason.Quota);
        }

        if (context.Quota.AtLeast(QuotaLevel.Prepare) && job.HandoffRequestedAt is null)
        {
            return context.Provider.SupportsHandoff
                ? new JobAction(JobActionKind.RequestHandoff, Reason: "Quota is low: asking for a handoff note.")
                : new JobAction(JobActionKind.WriteCheckpoint, JobStatus.Running, Reason: "Quota is low: checkpoint recorded.");
        }

        return JobAction.None;
    }

    /// <summary>Start or resume now if the quota and the workspace allow it; otherwise wait or block.</summary>
    private static JobAction TryContinue(Job job, JobContext context)
    {
        if (context.Quota.Blocked && !context.QuotaConfirmed && !job.ResumeNow)
        {
            return SuggestCredit(job, context) ?? Wait(job, context);
        }

        if (context.WorkspaceChanged && !job.ForceResume)
        {
            return JobAction.Set(JobStatus.BlockedWorkspace, "The repository changed since the checkpoint (branch or HEAD). Check it, then run `fermata resume <id> --force`.");
        }

        return new JobAction(job.HasSession ? JobActionKind.Resume : JobActionKind.Start);
    }

    private static JobAction Wait(Job job, JobContext context)
    {
        var until = context.Quota.ResumeAt ?? job.ResumeAt;
        return job.Status == JobStatus.WaitingQuota && until == job.ResumeAt
            ? JobAction.None
            : new JobAction(JobActionKind.WaitUntil, JobStatus.WaitingQuota, until, WaitReason(context));
    }

    /// <summary>Once per episode, when the user could end a long wait with a credit only they can redeem.</summary>
    private static JobAction? SuggestCredit(Job job, JobContext context)
    {
        if (context.Provider.ResetCredits != ResetCreditSupport.ManualWeb
            || job.CreditSuggestedAt is not null
            || !context.Quota.Blocked
            || context.Quota.ResumeAt is not { } until
            || until - context.Now < context.SuggestCreditAfter)
        {
            return null;
        }

        return new JobAction(JobActionKind.SuggestCredit, Until: until, Reason: "Long wait for the quota: a reset credit would end it.");
    }

    private static string WaitReason(JobContext context) =>
        context.Quota.CriticalWindows.Count == 0
            ? "Waiting for the usage limit to reset."
            : $"Waiting for the {string.Join(" and ", context.Quota.CriticalWindows.Select(Describe))} limit to reset.";

    private static string Describe(QuotaWindowKind kind) => kind switch
    {
        QuotaWindowKind.FiveHour => "5-hour",
        QuotaWindowKind.Weekly => "weekly",
        _ => "usage",
    };
}
