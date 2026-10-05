using Fermata.Core.Jobs;

namespace Fermata.Core.Tests;

public sealed class JobPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = Now.AddHours(3);

    private static readonly ProviderCapabilities Codex = new(ResetCreditSupport.Api, SupportsHandoff: false);
    private static readonly ProviderCapabilities Claude = new(ResetCreditSupport.ManualWeb, SupportsHandoff: true);

    private static Job NewJob(JobStatus status, bool session = true) => new()
    {
        Id = "job",
        Provider = JobProviderKind.Codex,
        Cwd = "/repo",
        Objective = "do it",
        Status = status,
        Session = session ? new SessionRef("thread-1") : null,
        CreatedAt = Now.AddHours(-1),
        UpdatedAt = Now.AddHours(-1),
    };

    private static QuotaAssessment Level(QuotaLevel level) =>
        new(level, level is QuotaLevel.Normal or QuotaLevel.Unknown ? null : Reset, [QuotaWindowKind.FiveHour], 3);

    private static JobContext Context(
        QuotaLevel quota = QuotaLevel.Normal,
        SessionState session = SessionState.Running,
        bool confirmed = false,
        bool workspaceChanged = false,
        ProviderCapabilities? provider = null,
        DateTimeOffset? now = null) =>
        new(Level(quota), confirmed, new SessionObservation(session), workspaceChanged, provider ?? Codex, now ?? Now, TimeSpan.FromHours(2));

    private static JobActionKind Kind(Job job, JobContext context) => JobPolicy.Decide(job, context).Kind;

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void Finished_jobs_never_act(JobStatus status) =>
        Assert.Equal(JobActionKind.None, Kind(NewJob(status), Context(QuotaLevel.Blocked, SessionState.LimitStopped)));

    [Fact]
    public void Backoff_holds_every_decision()
    {
        var job = NewJob(JobStatus.WaitingQuota) with { NextAttemptAt = Now.AddMinutes(1) };
        Assert.Equal(JobActionKind.None, Kind(job, Context(QuotaLevel.Normal, SessionState.LimitStopped)));
    }

    [Fact]
    public void Scheduled_job_waits_for_its_time_then_starts()
    {
        var job = NewJob(JobStatus.Scheduled, session: false) with { ResumeAt = Now.AddMinutes(10) };
        Assert.Equal(JobActionKind.None, Kind(job, Context(session: SessionState.Idle)));
        Assert.Equal(JobActionKind.Start, Kind(job, Context(session: SessionState.Idle, now: Now.AddMinutes(10))));
    }

    [Fact]
    public void Scheduled_job_whose_time_comes_during_a_limit_waits_for_the_reset()
    {
        var job = NewJob(JobStatus.Scheduled, session: false) with { ResumeAt = Now };
        var action = JobPolicy.Decide(job, Context(QuotaLevel.Blocked, SessionState.Idle));
        Assert.Equal(JobActionKind.WaitUntil, action.Kind);
        Assert.Equal(JobStatus.WaitingQuota, action.Status);
        Assert.Equal(Reset, action.Until);
    }

    [Fact]
    public void Running_with_normal_quota_does_nothing() =>
        Assert.Equal(JobActionKind.None, Kind(NewJob(JobStatus.Running), Context()));

    [Fact]
    public void Prepare_level_asks_for_a_handoff_once_when_the_provider_can()
    {
        var job = NewJob(JobStatus.Running);
        Assert.Equal(JobActionKind.RequestHandoff, Kind(job, Context(QuotaLevel.Prepare, provider: Claude)));
        Assert.Equal(JobActionKind.None, Kind(job with { HandoffRequestedAt = Now }, Context(QuotaLevel.Prepare, provider: Claude)));
    }

    [Fact]
    public void Prepare_level_writes_a_mechanical_checkpoint_when_the_provider_cannot_hand_off()
    {
        var action = JobPolicy.Decide(NewJob(JobStatus.Running), Context(QuotaLevel.Prepare, provider: Codex));
        Assert.Equal(JobActionKind.WriteCheckpoint, action.Kind);
        Assert.Equal(JobStatus.Running, action.Status);
    }

    [Theory]
    [InlineData(QuotaLevel.StopNewWork)]
    [InlineData(QuotaLevel.Blocked)]
    public void Low_quota_stops_at_the_turn_boundary(QuotaLevel level)
    {
        var action = JobPolicy.Decide(NewJob(JobStatus.Running), Context(level));
        Assert.Equal(JobActionKind.PauseAtTurnBoundary, action.Kind);
        Assert.Equal(JobStatus.Checkpointing, action.Status);
        Assert.Equal(StopReason.Quota, action.StopReason);
        Assert.Equal(Reset, action.Until);
    }

    [Fact]
    public void Unknown_quota_never_stops_a_running_job() =>
        Assert.Equal(JobActionKind.None, Kind(NewJob(JobStatus.Running), Context(QuotaLevel.Unknown)));

    [Fact]
    public void Checkpointing_waits_for_the_turn_then_checkpoints_into_the_quota_wait()
    {
        var job = NewJob(JobStatus.Checkpointing) with { StopReason = StopReason.Quota };
        Assert.Equal(JobActionKind.None, Kind(job, Context(QuotaLevel.StopNewWork, SessionState.Running)));

        var action = JobPolicy.Decide(job, Context(QuotaLevel.StopNewWork, SessionState.Idle));
        Assert.Equal(JobActionKind.WriteCheckpoint, action.Kind);
        Assert.Equal(JobStatus.WaitingQuota, action.Status);
        Assert.Equal(Reset, action.Until);
    }

    [Fact]
    public void A_user_pause_checkpoints_into_paused()
    {
        var job = NewJob(JobStatus.Checkpointing) with { StopReason = StopReason.User };
        var action = JobPolicy.Decide(job, Context(session: SessionState.Paused));
        Assert.Equal(JobActionKind.WriteCheckpoint, action.Kind);
        Assert.Equal(JobStatus.Paused, action.Status);
    }

    [Fact]
    public void Native_auto_resume_is_never_raced()
    {
        var running = JobPolicy.Decide(NewJob(JobStatus.Running), Context(QuotaLevel.Blocked, SessionState.NativeWaiting, provider: Claude));
        Assert.Equal(JobActionKind.WaitUntil, running.Kind);

        var waiting = NewJob(JobStatus.WaitingQuota) with { ResumeAt = Now.AddMinutes(-5) };
        Assert.Equal(JobActionKind.None, Kind(waiting, Context(QuotaLevel.Normal, SessionState.NativeWaiting, provider: Claude)));
    }

    [Fact]
    public void Native_resume_that_fired_marks_the_job_running()
    {
        var action = JobPolicy.Decide(NewJob(JobStatus.WaitingQuota), Context(QuotaLevel.Normal, SessionState.Running));
        Assert.Equal(JobActionKind.SetStatus, action.Kind);
        Assert.Equal(JobStatus.Running, action.Status);
    }

    [Fact]
    public void A_session_stopped_at_the_limit_is_checkpointed_and_waits()
    {
        var action = JobPolicy.Decide(NewJob(JobStatus.Running), Context(QuotaLevel.Blocked, SessionState.LimitStopped));
        Assert.Equal(JobActionKind.WriteCheckpoint, action.Kind);
        Assert.Equal(JobStatus.WaitingQuota, action.Status);
    }

    [Fact]
    public void Waiting_job_resumes_after_its_time_when_the_quota_is_not_blocked()
    {
        var job = NewJob(JobStatus.WaitingQuota) with { ResumeAt = Reset };
        Assert.Equal(JobActionKind.None, Kind(job, Context(QuotaLevel.Blocked, SessionState.LimitStopped)));
        Assert.Equal(JobActionKind.Resume, Kind(job, Context(QuotaLevel.Normal, SessionState.LimitStopped, now: Reset)));
    }

    [Fact]
    public void Confirmed_quota_resumes_before_the_reset_time_after_a_credit()
    {
        var job = NewJob(JobStatus.WaitingQuota) with { ResumeAt = Reset };
        Assert.Equal(JobActionKind.Resume, Kind(job, Context(QuotaLevel.Normal, SessionState.LimitStopped, confirmed: true)));
    }

    [Fact]
    public void Unconfirmed_quota_waits_for_the_reset_time()
    {
        // Claude cannot re-check its quota: the stale snapshot looks fine, but the time has not come.
        var job = NewJob(JobStatus.WaitingQuota) with { ResumeAt = Reset };
        Assert.Equal(JobActionKind.None, Kind(job, Context(QuotaLevel.Normal, SessionState.Gone, provider: Claude)));
    }

    [Fact]
    public void Still_blocked_at_the_reset_time_moves_the_wait()
    {
        var job = NewJob(JobStatus.WaitingQuota) with { ResumeAt = Now.AddMinutes(-1) };
        var action = JobPolicy.Decide(job, Context(QuotaLevel.Blocked, SessionState.LimitStopped));
        Assert.Equal(JobActionKind.WaitUntil, action.Kind);
        Assert.Equal(Reset, action.Until);

        Assert.Equal(JobActionKind.None, Kind(job with { ResumeAt = Reset }, Context(QuotaLevel.Blocked, SessionState.LimitStopped, now: Reset.AddMinutes(-1))));
    }

    [Fact]
    public void Resume_now_ignores_a_stale_limit()
    {
        var job = NewJob(JobStatus.Paused) with { ResumeNow = true };
        Assert.Equal(JobActionKind.Resume, Kind(job, Context(QuotaLevel.Blocked, SessionState.Gone, provider: Claude)));
    }

    [Fact]
    public void A_changed_workspace_blocks_the_resume_unless_forced()
    {
        var job = NewJob(JobStatus.WaitingQuota);
        var action = JobPolicy.Decide(job, Context(QuotaLevel.Normal, SessionState.LimitStopped, workspaceChanged: true));
        Assert.Equal(JobActionKind.SetStatus, action.Kind);
        Assert.Equal(JobStatus.BlockedWorkspace, action.Status);

        Assert.Equal(JobActionKind.Resume, Kind(job with { ForceResume = true }, Context(QuotaLevel.Normal, SessionState.LimitStopped, workspaceChanged: true)));
        Assert.Equal(JobActionKind.None, Kind(NewJob(JobStatus.BlockedWorkspace), Context(QuotaLevel.Normal, SessionState.Idle)));
    }

    [Fact]
    public void Paused_jobs_follow_their_plan()
    {
        var manual = NewJob(JobStatus.Paused) with { ResumeMode = ResumeMode.Manual };
        Assert.Equal(JobActionKind.None, Kind(manual, Context(session: SessionState.Paused)));

        var at = NewJob(JobStatus.Paused) with { ResumeMode = ResumeMode.At, ResumeAt = Now.AddHours(1) };
        Assert.Equal(JobActionKind.None, Kind(at, Context(session: SessionState.Paused)));
        Assert.Equal(JobActionKind.Resume, Kind(at, Context(session: SessionState.Paused, now: Now.AddHours(1))));

        var quota = NewJob(JobStatus.Paused) with { ResumeMode = ResumeMode.QuotaAvailable };
        Assert.Equal(JobActionKind.WaitUntil, Kind(quota, Context(QuotaLevel.Blocked, SessionState.Paused)));
        Assert.Equal(JobActionKind.Resume, Kind(quota, Context(QuotaLevel.Normal, SessionState.Paused)));
    }

    [Theory]
    [InlineData(SessionState.WaitingApproval, JobStatus.BlockedApproval)]
    [InlineData(SessionState.WaitingInput, JobStatus.BlockedUser)]
    public void Approvals_and_questions_block_the_job(SessionState session, JobStatus expected)
    {
        var action = JobPolicy.Decide(NewJob(JobStatus.Running), Context(session: session));
        Assert.Equal(JobActionKind.SetStatus, action.Kind);
        Assert.Equal(expected, action.Status);

        Assert.Equal(JobActionKind.None, Kind(NewJob(expected), Context(session: session)));
        Assert.Equal(JobStatus.Running, JobPolicy.Decide(NewJob(expected), Context(session: SessionState.Running)).Status);
    }

    [Fact]
    public void A_completed_session_completes_the_job()
    {
        foreach (var status in new[] { JobStatus.Running, JobStatus.Checkpointing, JobStatus.WaitingQuota, JobStatus.BlockedUser, JobStatus.Paused })
        {
            Assert.Equal(JobActionKind.Complete, Kind(NewJob(status) with { ResumeMode = ResumeMode.Manual }, Context(session: SessionState.Completed)));
        }
    }

    [Fact]
    public void A_session_that_vanished_without_a_limit_is_paused_for_the_user()
    {
        var action = JobPolicy.Decide(NewJob(JobStatus.Running), Context(QuotaLevel.Normal, SessionState.Gone));
        Assert.Equal(JobActionKind.WriteCheckpoint, action.Kind);
        Assert.Equal(JobStatus.Paused, action.Status);

        Assert.Equal(JobStatus.WaitingQuota, JobPolicy.Decide(NewJob(JobStatus.Running), Context(QuotaLevel.Blocked, SessionState.Gone)).Status);
    }

    [Fact]
    public void Unknown_session_state_changes_nothing() =>
        Assert.Equal(JobActionKind.None, Kind(NewJob(JobStatus.Running), Context(QuotaLevel.StopNewWork, SessionState.Unknown)));

    [Fact]
    public void A_long_claude_wait_suggests_a_manual_credit_once()
    {
        var job = NewJob(JobStatus.WaitingQuota) with { Provider = JobProviderKind.Claude, ResumeAt = Reset };
        var action = JobPolicy.Decide(job, Context(QuotaLevel.Blocked, SessionState.NativeWaiting, provider: Claude));
        Assert.Equal(JobActionKind.SuggestCredit, action.Kind);

        Assert.Equal(JobActionKind.None, Kind(job with { CreditSuggestedAt = Now }, Context(QuotaLevel.Blocked, SessionState.NativeWaiting, provider: Claude)));

        // Short waits and Codex (credits through the API, handled by the monitor) get no suggestion.
        Assert.Equal(JobActionKind.None, Kind(job, Context(QuotaLevel.Blocked, SessionState.NativeWaiting, provider: Claude, now: Reset.AddHours(-1))));
        Assert.NotEqual(JobActionKind.SuggestCredit, Kind(job, Context(QuotaLevel.Blocked, SessionState.LimitStopped, provider: Codex)));
    }
}
