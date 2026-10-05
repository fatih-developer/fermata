using Fermata.Core.Jobs;
using Fermata.Core.Policies;
using Fermata.Core.Ports;
using Microsoft.Extensions.Logging;

namespace Fermata.Platform.Jobs;

/// <summary>A job changed in a way worth telling the user (status, credit suggestion, failure).</summary>
public sealed record JobNotice(Job Job, JobEvent Event, bool Important);

/// <summary>
/// Drives every open job: reads quota and session state, asks <see cref="JobPolicy"/> what to do,
/// does it and records it. Runs in exactly one process (desktop app or <c>fermata daemon</c>),
/// guarded by <c>jobs.lock</c>. The loop checks the wall clock every couple of seconds instead of
/// trusting one long delay, so a reset time missed during sleep is acted on right after wake-up.
/// </summary>
public sealed partial class JobScheduler
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30),
    ];

    private readonly IJobStore _store;
    private readonly IReadOnlyDictionary<JobProviderKind, IJobProvider> _providers;
    private readonly CheckpointWriter _checkpoints;
    private readonly Func<JobsOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JobScheduler(
        IJobStore store,
        IEnumerable<IJobProvider> providers,
        CheckpointWriter checkpoints,
        Func<JobsOptions> options,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _store = store;
        _providers = providers.ToDictionary(p => p.Kind);
        _checkpoints = checkpoints;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised after every recorded event (on the scheduler's thread).</summary>
    public event Action<JobNotice>? Changed;

    public IJobStore Store => _store;

    /// <summary>True while <see cref="RunAsync"/> holds the scheduler lock (only then may this process step jobs).</summary>
    public bool IsActive { get; private set; }

    /// <summary>Runs until cancelled. Waits (quietly) while another process holds the lock.</summary>
    public async Task RunAsync(IResetLock jobsLock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobsLock);
        IDisposable? held = null;
        try
        {
            while (held is null)
            {
                held = jobsLock.TryAcquire();
                if (held is null)
                {
                    LogLockBusy(_logger);
                    await Task.Delay(TimeSpan.FromMinutes(1), _time, cancellationToken).ConfigureAwait(false);
                }
            }

            IsActive = true;
            var nextTick = DateTimeOffset.MinValue;
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = _time.GetUtcNow();
                if (now >= nextTick || _store.HasRequests())
                {
                    await TickAsync(cancellationToken).ConfigureAwait(false);
                    nextTick = _time.GetUtcNow() + TickInterval;
                }

                await Task.Delay(PollInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            IsActive = false;
            held?.Dispose();
        }
    }

    /// <summary>Carries out pending requests, then advances every open job by one step.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var request in _store.TakeRequests())
            {
                await ApplyCoreAsync(request, cancellationToken).ConfigureAwait(false);
            }

            foreach (var job in _store.List().Where(j => !j.IsFinished))
            {
                await StepCoreAsync(job, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One request, then one step of that job (the CLI uses this when no scheduler runs).</summary>
    public async Task<Job?> ApplyAsync(JobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await ApplyCoreAsync(request, cancellationToken).ConfigureAwait(false);
            return job is null || job.IsFinished ? job : await StepCoreAsync(job, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Saves a new job and takes its first step (start now, or wait for its time).</summary>
    public async Task<Job> AddAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _store.Save(job);
            Record(job, "created", job.Status == JobStatus.Scheduled && job.ResumeAt is { } at
                ? $"Scheduled for {at.ToLocalTime():yyyy-MM-dd HH:mm}."
                : job.Origin == JobOrigin.Adopted ? $"Adopted {job.Provider} session {job.Session?.Id}." : "Created.");
            return await StepCoreAsync(job, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Job> StepAsync(Job job, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StepCoreAsync(job, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Job?> ApplyCoreAsync(JobRequest request, CancellationToken cancellationToken)
    {
        var job = _store.Get(request.JobId);
        if (job is null)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        if (job.IsFinished)
        {
            Record(job, "ignored", $"{request.Kind} ignored: the job is {job.Status}.");
            return job;
        }

        try
        {
            switch (request.Kind)
            {
                case JobRequestKind.Start:
                    Record(job, "created", job.Status == JobStatus.Scheduled && job.ResumeAt is { } startAt
                        ? $"Scheduled for {startAt.ToLocalTime():yyyy-MM-dd HH:mm}."
                        : job.Origin == JobOrigin.Adopted ? $"Adopted {job.Provider} session {job.Session?.Id}." : "Created.");
                    return job;

                case JobRequestKind.Cancel:
                    if (job.HasSession && _providers.TryGetValue(job.Provider, out var cancelProvider))
                    {
                        await cancelProvider.CancelAsync(job, cancellationToken).ConfigureAwait(false);
                    }

                    job = await CheckpointAsync(job, "cancelled", now, cancellationToken).ConfigureAwait(false);
                    return Save(job with { Status = JobStatus.Cancelled, FinishedAt = now, BlockReason = null }, "cancelled", "Cancelled.", important: true);

                case JobRequestKind.Checkpoint:
                    job = await CheckpointAsync(job, "requested", now, cancellationToken).ConfigureAwait(false);
                    return Save(job, "checkpoint", $"Checkpoint written: {job.LastCheckpoint?.Path}");

                case JobRequestKind.Pause:
                    return await PauseAsync(job, request, now, cancellationToken).ConfigureAwait(false);

                case JobRequestKind.Resume:
                    var scheduled = job.Status == JobStatus.Scheduled;
                    var resumed = job with
                    {
                        // Every other state becomes Paused with the new plan; the policy then resumes (or waits).
                        Status = scheduled ? JobStatus.Scheduled : JobStatus.Paused,
                        ResumeMode = request.Mode,
                        ResumeAt = request.Mode == ResumeMode.At ? request.At : scheduled && !request.Now ? job.ResumeAt : null,
                        StopReason = StopReason.User,
                        ResumeNow = request.Now,
                        ForceResume = request.Force || job.ForceResume,
                        NextAttemptAt = null,
                    };
                    return Save(resumed, "resume-requested", request.Mode switch
                    {
                        ResumeMode.At => $"Will resume at {request.At?.ToLocalTime():yyyy-MM-dd HH:mm}.",
                        _ when request.Now => "Resuming now.",
                        _ => "Will resume when the quota allows it.",
                    });
            }
        }
        catch (JobProviderException ex)
        {
            return Fail(job, ex, now);
        }

        return job;
    }

    private async Task<Job> PauseAsync(Job job, JobRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var mode = request.At is null ? ResumeMode.Manual : ResumeMode.At;
        var paused = job with { ResumeMode = mode, ResumeAt = request.At, StopReason = StopReason.User, ResumeNow = false };
        if (job.Status is JobStatus.Running or JobStatus.BlockedApproval or JobStatus.BlockedUser
            && job.HasSession
            && _providers.TryGetValue(job.Provider, out var provider))
        {
            var stopped = await provider.PauseAsync(paused, cancellationToken).ConfigureAwait(false);
            if (!stopped)
            {
                return Save(paused with { Status = JobStatus.Checkpointing, BlockReason = "Pausing after the current turn." }, "pausing", "Pausing after the current turn.");
            }

            paused = await CheckpointAsync(paused, "paused", now, cancellationToken).ConfigureAwait(false);
        }

        return Save(paused with { Status = JobStatus.Paused, BlockReason = "Paused." }, "paused", request.At is { } at ? $"Paused until {at.ToLocalTime():yyyy-MM-dd HH:mm}." : "Paused.", important: true);
    }

    private async Task<Job> StepCoreAsync(Job job, CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(job.Provider, out var provider))
        {
            return job;
        }

        var options = _options();
        var now = _time.GetUtcNow();
        var snapshot = provider.GetQuota();
        var quota = QuotaPolicy.Assess(snapshot, options.QuotaPolicy, now);
        try
        {
            var session = job.NextAttemptAt > now
                ? SessionObservation.Unknown
                : job.HasSession ? await provider.GetSessionStateAsync(job, cancellationToken).ConfigureAwait(false) : SessionObservation.None;
            var workspaceChanged = job.Status is JobStatus.Paused or JobStatus.WaitingQuota or JobStatus.Scheduled
                && await _checkpoints.HasChangedAsync(job.Cwd, job.LastCheckpoint, cancellationToken).ConfigureAwait(false);

            var context = new JobContext(
                quota,
                snapshot?.UsageAllowed == true,
                session,
                workspaceChanged,
                ProviderCapabilities.Of(provider),
                now,
                TimeSpan.FromMinutes(options.Claude.SuggestResetAfterMinutes));
            var action = JobPolicy.Decide(job, context);
            if (snapshot is not null && action.Kind != JobActionKind.None)
            {
                job = job with { LastQuota = snapshot };
            }

            return await ExecuteAsync(job, action, provider, session, now, options, cancellationToken).ConfigureAwait(false);
        }
        catch (JobProviderException ex)
        {
            return Fail(job, ex, now);
        }
    }

    private async Task<Job> ExecuteAsync(Job job, JobAction action, IJobProvider provider, SessionObservation session, DateTimeOffset now, JobsOptions options, CancellationToken cancellationToken)
    {
        switch (action.Kind)
        {
            case JobActionKind.None:
                return job;

            case JobActionKind.Start:
                await _checkpoints.EnsureExcludedAsync(job.Cwd, cancellationToken).ConfigureAwait(false);
                var started = await provider.StartAsync(job, cancellationToken).ConfigureAwait(false);
                return Save(Continued(job) with { Session = started }, "started", $"Started {job.Provider} session {started.ShortId ?? started.Id}.", important: true);

            case JobActionKind.Resume:
                await _checkpoints.EnsureExcludedAsync(job.Cwd, cancellationToken).ConfigureAwait(false);
                var moved = await provider.ResumeAsync(job, JobPrompts.Resume(job), cancellationToken).ConfigureAwait(false);
                ClearHandoffMarker(job);
                return Save(Continued(job) with { Session = moved ?? job.Session }, "resumed", job.ForceResume ? "Resumed (workspace check skipped)." : "Resumed.", important: true);

            case JobActionKind.RequestHandoff:
                await _checkpoints.EnsureExcludedAsync(job.Cwd, cancellationToken).ConfigureAwait(false);
                await provider.RequestHandoffAsync(job, cancellationToken).ConfigureAwait(false);
                return Save(job with { HandoffRequestedAt = now }, "handoff-requested", action.Reason ?? "Handoff requested.");

            case JobActionKind.PauseAtTurnBoundary:
                var pausing = job with { Status = JobStatus.Checkpointing, StopReason = action.StopReason, ResumeAt = action.Until ?? job.ResumeAt, BlockReason = action.Reason };
                await provider.PauseAsync(pausing, cancellationToken).ConfigureAwait(false);
                return Save(pausing, "stopping", action.Reason ?? "Stopping after the current turn.", important: true);

            case JobActionKind.WriteCheckpoint:
                var target = action.Status ?? job.Status;
                var checkpointed = await CheckpointAsync(job, action.Reason ?? target.ToString(), now, cancellationToken).ConfigureAwait(false);
                checkpointed = checkpointed with
                {
                    Status = target,
                    ResumeAt = target == JobStatus.Running ? checkpointed.ResumeAt : action.Until,
                    ResumeMode = target switch
                    {
                        JobStatus.WaitingQuota => ResumeMode.QuotaAvailable,
                        // Paused by the user through Fermata keeps the plan they gave; any other pause waits for them.
                        JobStatus.Paused => job.StopReason == StopReason.User ? job.ResumeMode : ResumeMode.Manual,
                        _ => checkpointed.ResumeMode,
                    },
                    BlockReason = target == JobStatus.Running ? null : action.Reason,
                    StopReason = action.StopReason ?? checkpointed.StopReason,
                    HandoffRequestedAt = target == JobStatus.Running ? now : checkpointed.HandoffRequestedAt,
                };
                return Save(checkpointed, target == job.Status ? "checkpoint" : Kind(target), Describe(target, action, session), important: target != job.Status);

            case JobActionKind.WaitUntil:
                return Save(
                    job with { Status = JobStatus.WaitingQuota, ResumeAt = action.Until, ResumeMode = job.ResumeMode == ResumeMode.Manual ? ResumeMode.QuotaAvailable : job.ResumeMode, BlockReason = action.Reason },
                    "waiting",
                    Describe(JobStatus.WaitingQuota, action, session),
                    important: job.Status != JobStatus.WaitingQuota);

            case JobActionKind.SetStatus:
                var status = action.Status ?? job.Status;
                var because = session.Detail is { } detail && action.Reason is { } said ? $"{said} ({detail})" : action.Reason;
                var updated = job with
                {
                    Status = status,
                    BlockReason = status == JobStatus.Running ? null : because,
                    ResumeAt = status == JobStatus.Running ? null : job.ResumeAt,
                };
                return Save(updated, Kind(status), because ?? status.ToString(), important: status is not JobStatus.Running || job.Status == JobStatus.WaitingQuota);

            case JobActionKind.SuggestCredit:
                return Save(job with { CreditSuggestedAt = now }, "credit-suggested", $"{job.DisplayName} waits for {job.Provider} until {action.Until?.ToLocalTime():HH:mm ddd}. If you have a reset credit, use it on {options.Claude.LimitResetsUrl} (\"Limit resets\").", important: true);

            case JobActionKind.Complete:
                var done = await CheckpointAsync(job, "completed", now, cancellationToken).ConfigureAwait(false);
                return Save(done with { Status = JobStatus.Completed, FinishedAt = now, BlockReason = null, ResumeAt = null }, "completed", "Completed.", important: true);
        }

        return job;
    }

    private static Job Continued(Job job) => job with
    {
        Status = JobStatus.Running,
        ResumeMode = ResumeMode.QuotaAvailable,
        ResumeAt = null,
        ResumeNow = false,
        ForceResume = false,
        StopReason = null,
        BlockReason = null,
        HandoffRequestedAt = null,
        CreditSuggestedAt = null,
        FailureCount = 0,
        NextAttemptAt = null,
        LastError = null,
    };

    private async Task<Job> CheckpointAsync(Job job, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var info = await _checkpoints.WriteAsync(job, _store.DirectoryOf(job.Id), reason, _options().SavePatch, now, cancellationToken).ConfigureAwait(false);
            return job with { LastCheckpoint = info };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCheckpointFailed(_logger, ex, job.Id);
            return job;
        }
    }

    private void ClearHandoffMarker(Job job)
    {
        try
        {
            File.Delete(Path.Combine(_store.DirectoryOf(job.Id), HandoffMarker));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Created by the Claude Stop hook once it asked for the handoff note in this episode.</summary>
    public const string HandoffMarker = "handoff-injected";

    private Job Fail(Job job, JobProviderException error, DateTimeOffset now)
    {
        LogProviderFailed(_logger, error, job.Id);
        if (error.Permanent)
        {
            return Save(job with { Status = JobStatus.Failed, FinishedAt = now, BlockReason = error.Message, LastError = error.Message }, "failed", error.Message, important: true);
        }

        var count = job.FailureCount + 1;
        var wait = Backoff[Math.Min(count - 1, Backoff.Length - 1)];
        return Save(job with { FailureCount = count, NextAttemptAt = now + wait, LastError = error.Message }, "retry", $"{error.Message} Retrying in {Format(wait)}.", important: count == 3);
    }

    private Job Save(Job job, string kind, string message, bool important = false)
    {
        var now = _time.GetUtcNow();
        job = job with { UpdatedAt = now };
        _store.Save(job);
        Record(job, kind, message, important);
        return job;
    }

    private void Record(Job job, string kind, string message, bool important = false)
    {
        var jobEvent = new JobEvent(_time.GetUtcNow(), kind, message);
        _store.AppendEvent(job.Id, jobEvent);
        LogJobEvent(_logger, job.Id, kind, message);
        try
        {
            Changed?.Invoke(new JobNotice(job, jobEvent, important));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogListenerFailed(_logger, ex);
        }
    }

    private static string Kind(JobStatus status) => status switch
    {
        JobStatus.WaitingQuota => "waiting",
        JobStatus.BlockedApproval => "needs-approval",
        JobStatus.BlockedUser => "needs-input",
        JobStatus.BlockedWorkspace => "workspace-changed",
        JobStatus.Running => "running",
        JobStatus.Paused => "paused",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static string Describe(JobStatus status, JobAction action, SessionObservation session)
    {
        var text = action.Reason ?? status.ToString();
        if (status == JobStatus.WaitingQuota && action.Until is { } until)
        {
            text += $" Resumes around {until.ToLocalTime():yyyy-MM-dd HH:mm}.";
        }

        return session.Detail is { } detail ? $"{text} ({detail})" : text;
    }

    private static string Format(TimeSpan span) => span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} min" : $"{span.TotalSeconds:0} s";

    [LoggerMessage(500, LogLevel.Information, "Job {JobId}: {Kind} {Message}")]
    private static partial void LogJobEvent(ILogger logger, string jobId, string kind, string message);

    [LoggerMessage(501, LogLevel.Warning, "Job {JobId}: provider call failed")]
    private static partial void LogProviderFailed(ILogger logger, Exception error, string jobId);

    [LoggerMessage(502, LogLevel.Warning, "Job {JobId}: checkpoint failed")]
    private static partial void LogCheckpointFailed(ILogger logger, Exception error, string jobId);

    [LoggerMessage(503, LogLevel.Debug, "Another process runs the job scheduler; waiting")]
    private static partial void LogLockBusy(ILogger logger);

    [LoggerMessage(504, LogLevel.Warning, "A job listener failed")]
    private static partial void LogListenerFailed(ILogger logger, Exception error);
}
