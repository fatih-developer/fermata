using Fermata.Core.Jobs;
using Fermata.Core.Policies;
using Fermata.Platform.Jobs;
using Fermata.Platform.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fermata.Core.Tests;

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Scriptable provider: tests set the session state and quota, and read back the calls.</summary>
internal sealed class FakeJobProvider(JobProviderKind kind = JobProviderKind.Codex, bool handoff = false) : IJobProvider
{
    public JobProviderKind Kind => kind;

    public ResetCreditSupport ResetCredits => kind == JobProviderKind.Claude ? ResetCreditSupport.ManualWeb : ResetCreditSupport.Api;

    public bool SupportsHandoff => handoff;

    public SessionState State { get; set; } = SessionState.Running;

    public QuotaSnapshot? Quota { get; set; }

    public bool PauseStopsImmediately { get; set; }

    public JobProviderException? FailNext { get; set; }

    public List<string> Calls { get; } = [];

    public Task<SessionRef> StartAsync(Job job, CancellationToken cancellationToken)
    {
        Throw();
        Calls.Add($"start {job.Id}");
        State = SessionState.Running;
        return Task.FromResult(new SessionRef("session-" + job.Id, "bg1"));
    }

    public Task<AdoptedSession> AdoptAsync(string? sessionId, string? objective, string cwd, CancellationToken cancellationToken) =>
        Task.FromResult(new AdoptedSession(new SessionRef(sessionId ?? "latest"), cwd, objective ?? "adopted"));

    public Task<SessionObservation> GetSessionStateAsync(Job job, CancellationToken cancellationToken)
    {
        Throw();
        return Task.FromResult(new SessionObservation(State));
    }

    public Task<bool> PauseAsync(Job job, CancellationToken cancellationToken)
    {
        Calls.Add($"pause {job.StopReason}");
        if (PauseStopsImmediately)
        {
            State = SessionState.Paused;
        }

        return Task.FromResult(PauseStopsImmediately);
    }

    public Task<SessionRef?> ResumeAsync(Job job, string prompt, CancellationToken cancellationToken)
    {
        Throw();
        Calls.Add("resume");
        State = SessionState.Running;
        return Task.FromResult<SessionRef?>(null);
    }

    public Task<bool> RequestHandoffAsync(Job job, CancellationToken cancellationToken)
    {
        Calls.Add("handoff");
        return Task.FromResult(handoff);
    }

    public Task CancelAsync(Job job, CancellationToken cancellationToken)
    {
        Calls.Add("cancel");
        return Task.CompletedTask;
    }

    public QuotaSnapshot? GetQuota() => Quota;

    private void Throw()
    {
        if (FailNext is { } error)
        {
            FailNext = null;
            throw error;
        }
    }
}

public sealed class JobSchedulerTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("fermata-jobs-").FullName;
    private readonly ManualTime _time = new(Start);
    private readonly FakeJobProvider _provider = new();
    private readonly JsonJobStore _store;
    private readonly JobScheduler _scheduler;
    private readonly List<JobNotice> _notices = [];

    public JobSchedulerTests()
    {
        _store = new JsonJobStore(Path.Combine(_dir, "jobs"));
        _scheduler = new JobScheduler(_store, [_provider], new CheckpointWriter(), () => new JobsOptions(), _time, NullLogger.Instance);
        _scheduler.Changed += _notices.Add;
    }

    public void Dispose()
    {
        // git marks its objects read-only, which Directory.Delete refuses on Windows.
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private Job NewJob(JobStatus status = JobStatus.Scheduled, DateTimeOffset? at = null) => new()
    {
        Id = "fix-tests",
        Provider = JobProviderKind.Codex,
        Cwd = _dir,
        Objective = "Fix the failing tests",
        Status = status,
        ResumeAt = at,
        CreatedAt = _time.Now,
        UpdatedAt = _time.Now,
    };

    private QuotaSnapshot Quota(double used, bool? allowed = null, int resetMinutes = 120) => new()
    {
        Windows = [new QuotaWindow(QuotaWindowKind.FiveHour, used, _time.Now.AddMinutes(resetMinutes))],
        UsageAllowed = allowed,
        Source = "test",
        CapturedAt = _time.Now,
    };

    private Job Current => _store.Get("fix-tests")!;

    [Fact]
    public async Task A_new_job_starts_right_away()
    {
        var job = await _scheduler.AddAsync(NewJob(), CancellationToken.None);

        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal("session-fix-tests", job.Session!.Id);
        Assert.Equal(["start fix-tests"], _provider.Calls);
        Assert.Contains(_store.ReadEvents(job.Id, 10), e => e.Kind == "started");
    }

    [Fact]
    public async Task A_scheduled_job_starts_when_its_time_comes_even_after_a_sleep()
    {
        await _scheduler.AddAsync(NewJob(at: Start.AddHours(2)), CancellationToken.None);
        await _scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Scheduled, Current.Status);

        _time.Now = Start.AddHours(9); // laptop slept through the start time
        await _scheduler.TickAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Running, Current.Status);
    }

    [Fact]
    public async Task Full_quota_cycle_stop_checkpoint_wait_resume()
    {
        await _scheduler.AddAsync(NewJob(), CancellationToken.None);

        _provider.Quota = Quota(96);
        await _scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Checkpointing, Current.Status);
        Assert.Contains("pause Quota", _provider.Calls);

        _provider.State = SessionState.Paused; // the turn ended
        await _scheduler.TickAsync(CancellationToken.None);
        var waiting = Current;
        Assert.Equal(JobStatus.WaitingQuota, waiting.Status);
        Assert.NotNull(waiting.LastCheckpoint);
        Assert.True(File.Exists(waiting.LastCheckpoint!.Path));
        Assert.Equal(Start.AddMinutes(120).AddSeconds(90), waiting.ResumeAt);

        _time.Now = Start.AddMinutes(150);
        _provider.Quota = Quota(0, allowed: true);
        await _scheduler.TickAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Running, Current.Status);
        Assert.Contains("resume", _provider.Calls);
        Assert.Contains(_notices, n => n.Important && n.Event.Kind == "resumed");
    }

    [Fact]
    public async Task Provider_failures_back_off_and_recover()
    {
        await _scheduler.AddAsync(NewJob(), CancellationToken.None);
        _provider.FailNext = new JobProviderException("daemon down");

        await _scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(1, Current.FailureCount);
        Assert.Equal(Start.AddSeconds(30), Current.NextAttemptAt);

        _provider.FailNext = new JobProviderException("still down");
        _time.Now = Start.AddSeconds(31);
        await _scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(2, Current.FailureCount);
        Assert.Equal(_time.Now.AddMinutes(1), Current.NextAttemptAt);
    }

    [Fact]
    public async Task A_permanent_failure_fails_the_job()
    {
        _provider.FailNext = new JobProviderException("claude not found", permanent: true);
        var job = await _scheduler.AddAsync(NewJob(), CancellationToken.None);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("claude not found", job.BlockReason);
    }

    [Fact]
    public async Task Pause_and_resume_requests_from_the_cli()
    {
        await _scheduler.AddAsync(NewJob(), CancellationToken.None);
        _provider.PauseStopsImmediately = true;

        var paused = await _scheduler.ApplyAsync(new JobRequest("fix-tests", JobRequestKind.Pause, _time.Now), CancellationToken.None);
        Assert.Equal(JobStatus.Paused, paused!.Status);
        Assert.Equal(ResumeMode.Manual, paused.ResumeMode);

        await _scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Paused, Current.Status);

        var resumed = await _scheduler.ApplyAsync(new JobRequest("fix-tests", JobRequestKind.Resume, _time.Now), CancellationToken.None);
        Assert.Equal(JobStatus.Running, resumed!.Status);
    }

    [Fact]
    public async Task Queued_requests_are_taken_on_the_next_tick()
    {
        await _scheduler.AddAsync(NewJob(), CancellationToken.None);
        var request = new JobRequest("fix-tests", JobRequestKind.Cancel, _time.Now);
        _store.Enqueue(request);
        Assert.True(_store.HasRequests());
        Assert.True(_store.IsPending(request));

        await _scheduler.TickAsync(CancellationToken.None);

        Assert.False(_store.HasRequests());
        Assert.Equal(JobStatus.Cancelled, Current.Status);
        Assert.Contains("cancel", _provider.Calls);
    }

    [Fact]
    public async Task Pause_with_a_resume_time_continues_on_its_own()
    {
        await _scheduler.AddAsync(NewJob(), CancellationToken.None);
        _provider.PauseStopsImmediately = true;
        await _scheduler.ApplyAsync(new JobRequest("fix-tests", JobRequestKind.Pause, _time.Now, ResumeMode.At, Start.AddHours(1)), CancellationToken.None);

        await _scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Paused, Current.Status);

        _time.Now = Start.AddHours(1);
        await _scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(JobStatus.Running, Current.Status);
    }

    [Fact]
    public async Task Store_survives_a_corrupt_job_file_through_the_backup()
    {
        await _scheduler.AddAsync(NewJob(), CancellationToken.None);
        _store.Save(Current with { Name = "second save" });
        File.WriteAllText(Path.Combine(_store.DirectoryOf("fix-tests"), "job.json"), "{ torn");

        Assert.NotNull(_store.Get("fix-tests"));
        Assert.Single(_store.List());
    }

    [Fact]
    public void Job_ids_are_slugs_and_unique()
    {
        Assert.Equal("fix-the-ci-build", JobIds.Slug("Fix the CI build!"));
        Assert.Equal("guncelle-cagri-akisini", JobIds.Slug("Güncelle çağrı akışını"));
        Assert.Equal("job", JobIds.Slug("!!!"));
        Assert.Equal("a-2", JobIds.Unique("a", id => id is "a"));
        Assert.True(JobIds.Slug(new string('x', 100)).Length <= JobIds.MaxLength);
    }

    [Fact]
    public async Task Workspace_changes_block_the_resume_until_forced()
    {
        var git = ProcessRunner.Instance.FindOnPath("git");
        if (git is null)
        {
            return; // covered on machines with git
        }

        var repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(repo);
        async Task Git(params string[] args) => Assert.True((await ProcessRunner.Instance.RunAsync(git, ["-C", repo, "-c", "user.name=t", "-c", "user.email=t@t", .. args], TimeSpan.FromSeconds(20), CancellationToken.None)).Succeeded);
        await Git("init", "-q");
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "1");
        await Git("add", ".");
        await Git("commit", "-qm", "one");

        await _scheduler.AddAsync(NewJob() with { Cwd = repo }, CancellationToken.None);
        Assert.Contains(".fermata/", await File.ReadAllTextAsync(Path.Combine(repo, ".git", "info", "exclude")), StringComparison.Ordinal);

        _provider.Quota = Quota(100);
        _provider.State = SessionState.LimitStopped;
        await _scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(JobStatus.WaitingQuota, Current.Status);

        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "2");
        await Git("commit", "-qam", "two");

        _time.Now = Start.AddHours(3);
        _provider.Quota = Quota(0, allowed: true, resetMinutes: 300);
        await _scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(JobStatus.BlockedWorkspace, Current.Status);

        await _scheduler.ApplyAsync(new JobRequest("fix-tests", JobRequestKind.Resume, _time.Now, Force: true), CancellationToken.None);
        Assert.Equal(JobStatus.Running, Current.Status);
        Assert.False(Current.ForceResume);
    }
}
