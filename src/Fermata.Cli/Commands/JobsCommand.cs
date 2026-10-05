using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fermata.Cli.Output;
using Fermata.Core.Jobs;
using Fermata.Platform.Jobs;

namespace Fermata.Cli.Commands;

/// <summary>
/// <c>fermata run/adopt/jobs/job/pause/resume/cancel/checkpoint</c>. When no scheduler runs (no app,
/// no daemon), the command does the work itself under the scheduler lock; otherwise it hands a
/// request to the running scheduler and waits briefly for it.
/// </summary>
internal static class JobsCommand
{
    private static readonly TimeSpan HandOffWait = TimeSpan.FromSeconds(20);

    public static async Task<int> RunAsync(
        string provider,
        string? objective,
        string? goalFile,
        string? cwd,
        string? name,
        string? at,
        string? inText,
        string? approvals,
        CancellationToken cancellationToken)
    {
        var kind = ParseProvider(provider);
        if (objective is null == goalFile is null)
        {
            Console.Error.WriteLine("Give the task with exactly one of --objective \"…\" or --goal FILE.");
            return ExitCodes.Error;
        }

        objective ??= (await File.ReadAllTextAsync(goalFile!, cancellationToken).ConfigureAwait(false)).Trim();
        if (objective.Length == 0)
        {
            Console.Error.WriteLine("The objective is empty.");
            return ExitCodes.Error;
        }

        var directory = Path.GetFullPath(cwd ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"Folder not found: {directory}");
            return ExitCodes.Error;
        }

        var runtime = JobRuntime.Load();
        var now = DateTimeOffset.UtcNow;
        var startAt = TimeArgs.Resolve(at, inText, now);
        var job = new Job
        {
            Id = JobIds.Unique(name ?? objective, runtime.Store.Exists),
            Name = name,
            Provider = kind,
            Cwd = directory,
            Objective = objective,
            Status = JobStatus.Scheduled,
            ResumeMode = ResumeMode.At,
            ResumeAt = startAt,
            Approvals = approvals,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var result = await AddAsync(runtime, job, cancellationToken).ConfigureAwait(false);
        PrintJob(result, runtime.Store);
        return result.Status == JobStatus.Failed ? ExitCodes.Error : ExitCodes.Ok;
    }

    public static async Task<int> AdoptAsync(string provider, string? id, bool latest, string? objective, string? name, CancellationToken cancellationToken)
    {
        if (id is null && !latest)
        {
            Console.Error.WriteLine("Name the session to adopt, or pass --latest.");
            return ExitCodes.Error;
        }

        var kind = ParseProvider(provider);
        var runtime = JobRuntime.Load();
        AdoptedSession adopted;
        try
        {
            adopted = await runtime.Provider(kind).AdoptAsync(id, objective, Directory.GetCurrentDirectory(), cancellationToken).ConfigureAwait(false);
        }
        catch (JobProviderException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodes.Error;
        }

        if (runtime.Store.List().FirstOrDefault(j => !j.IsFinished && j.Session?.Id == adopted.Session.Id) is { } existing)
        {
            Console.Error.WriteLine($"That session is already job '{existing.Id}'.");
            return ExitCodes.Error;
        }

        var now = DateTimeOffset.UtcNow;
        var job = new Job
        {
            Id = JobIds.Unique(name ?? adopted.Objective, runtime.Store.Exists),
            Name = name,
            Provider = kind,
            Origin = JobOrigin.Adopted,
            Cwd = adopted.Cwd,
            Session = adopted.Session,
            Objective = adopted.Objective,
            Status = JobStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var result = await AddAsync(runtime, job, cancellationToken).ConfigureAwait(false);
        PrintJob(result, runtime.Store);
        if (kind == JobProviderKind.Claude && !new Fermata.Claude.ClaudeSettingsInstaller(Fermata.Claude.ClaudeSettingsInstaller.DefaultClaudeHome(), runtime.ClaudeState).GetStatus().Installed)
        {
            Console.WriteLine();
            Console.WriteLine("Note: Fermata's Claude hooks are not installed, so it cannot see limits or prompts. Run `fermata claude install`.");
        }

        return ExitCodes.Ok;
    }

    public static int List(bool json, bool all)
    {
        var runtime = JobRuntime.Load();
        var jobs = runtime.Store.List().Where(j => all || !j.IsFinished || j.FinishedAt > DateTimeOffset.UtcNow.AddDays(-1)).ToList();
        if (json)
        {
            Console.WriteLine(new JsonArray([.. jobs.Select(j => (JsonNode)ToJson(j))]).ToJsonString(JsonOptions.Indented));
            return ExitCodes.Ok;
        }

        if (jobs.Count == 0)
        {
            Console.WriteLine("No jobs. Start one with `fermata run --provider codex|claude --objective \"…\"`.");
            return ExitCodes.Ok;
        }

        var now = DateTimeOffset.UtcNow;
        Console.WriteLine($"{"ID",-28} {"PROVIDER",-8} {"STATUS",-16} {"NEXT",-18} OBJECTIVE");
        foreach (var job in jobs)
        {
            Console.WriteLine($"{Cut(job.Id, 28),-28} {job.Provider.ToString().ToLowerInvariant(),-8} {StatusText(job.Status),-16} {Next(job, now),-18} {Cut(job.Objective.ReplaceLineEndings(" "), 50)}");
        }

        return ExitCodes.Ok;
    }

    public static int Show(string id, bool json)
    {
        var runtime = JobRuntime.Load();
        if (runtime.Store.Get(id) is not { } job)
        {
            Console.Error.WriteLine($"No job '{id}'. See `fermata jobs`.");
            return ExitCodes.Error;
        }

        if (json)
        {
            var node = ToJson(job);
            node["events"] = new JsonArray([.. runtime.Store.ReadEvents(id, 50).Select(e => (JsonNode)new JsonObject { ["at"] = e.At, ["kind"] = e.Kind, ["message"] = e.Message })]);
            Console.WriteLine(node.ToJsonString(JsonOptions.Indented));
            return ExitCodes.Ok;
        }

        PrintJob(job, runtime.Store, events: 15);
        return ExitCodes.Ok;
    }

    public static Task<int> PauseAsync(string id, string? at, string? inText, CancellationToken cancellationToken)
    {
        var resumeAt = TimeArgs.Resolve(at, inText, DateTimeOffset.UtcNow);
        return SubmitAsync(new JobRequest(id, JobRequestKind.Pause, DateTimeOffset.UtcNow, resumeAt is null ? ResumeMode.Manual : ResumeMode.At, resumeAt), cancellationToken);
    }

    public static Task<int> ResumeAsync(string id, string? at, string? inText, bool whenQuota, bool now, bool force, CancellationToken cancellationToken)
    {
        var resumeAt = TimeArgs.Resolve(at, inText, DateTimeOffset.UtcNow);
        if (resumeAt is not null && (whenQuota || now))
        {
            throw new FormatException("--at/--in cannot be combined with --now or --when-quota-available.");
        }

        var mode = resumeAt is not null ? ResumeMode.At : ResumeMode.QuotaAvailable;
        return SubmitAsync(new JobRequest(id, JobRequestKind.Resume, DateTimeOffset.UtcNow, mode, resumeAt, Now: now, Force: force), cancellationToken);
    }

    public static Task<int> CancelAsync(string id, CancellationToken cancellationToken) =>
        SubmitAsync(new JobRequest(id, JobRequestKind.Cancel, DateTimeOffset.UtcNow), cancellationToken);

    public static Task<int> CheckpointAsync(string id, CancellationToken cancellationToken) =>
        SubmitAsync(new JobRequest(id, JobRequestKind.Checkpoint, DateTimeOffset.UtcNow), cancellationToken);

    private static async Task<int> SubmitAsync(JobRequest request, CancellationToken cancellationToken)
    {
        var runtime = JobRuntime.Load();
        if (runtime.Store.Get(request.JobId) is null)
        {
            Console.Error.WriteLine($"No job '{request.JobId}'. See `fermata jobs`.");
            return ExitCodes.Error;
        }

        Job? job;
        using (var held = runtime.Lock.TryAcquire())
        {
            if (held is not null)
            {
                job = await runtime.CreateScheduler().ApplyAsync(request, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                runtime.Store.Enqueue(request);
                job = await WaitForSchedulerAsync(runtime.Store, request, cancellationToken).ConfigureAwait(false);
            }
        }

        if (job is not null)
        {
            PrintJob(job, runtime.Store, events: 3);
        }

        return ExitCodes.Ok;
    }

    /// <summary>Saves the job and makes its first step here, or hands it to the running scheduler.</summary>
    private static async Task<Job> AddAsync(JobRuntime runtime, Job job, CancellationToken cancellationToken)
    {
        using var held = runtime.Lock.TryAcquire();
        if (held is not null)
        {
            var result = await runtime.CreateScheduler().AddAsync(job, cancellationToken).ConfigureAwait(false);
            if (!result.IsFinished)
            {
                Console.WriteLine("Note: neither the Fermata app nor `fermata daemon` is running, so nobody watches this job yet. Start one of them.");
                Console.WriteLine();
            }

            return result;
        }

        runtime.Store.Save(job);
        var request = new JobRequest(job.Id, JobRequestKind.Start, DateTimeOffset.UtcNow);
        runtime.Store.Enqueue(request);
        return await WaitForSchedulerAsync(runtime.Store, request, cancellationToken).ConfigureAwait(false) ?? job;
    }

    private static async Task<Job?> WaitForSchedulerAsync(JsonJobStore store, JobRequest request, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + HandOffWait;
        while (store.IsPending(request) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        if (store.IsPending(request))
        {
            Console.WriteLine("The running Fermata scheduler has not picked this up yet; it will shortly.");
            return store.Get(request.JobId);
        }

        // The scheduler takes the request, then steps the job: wait for the step's own event.
        var stepDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTimeOffset.UtcNow < stepDeadline)
        {
            var last = store.ReadEvents(request.JobId, 1).LastOrDefault();
            if (last is not null && last.At >= request.RequestedAt && last.Kind is not ("resume-requested" or "created"))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        return store.Get(request.JobId);
    }

    private static JobProviderKind ParseProvider(string provider) =>
        Enum.Parse<JobProviderKind>(provider, ignoreCase: true);

    internal static void PrintJob(Job job, JsonJobStore store, int events = 0)
    {
        var now = DateTimeOffset.UtcNow;
        Console.WriteLine($"Job        {job.Id}{(job.Name is not null && job.Name != job.Id ? $" ({job.Name})" : "")}");
        Console.WriteLine($"Provider   {job.Provider.ToString().ToLowerInvariant()}{(job.Origin == JobOrigin.Adopted ? " (adopted)" : "")}");
        Console.WriteLine($"Status     {StatusText(job.Status)}{(job.BlockReason is { } reason ? $": {reason}" : "")}");
        if (Next(job, now) is { Length: > 0 } next)
        {
            Console.WriteLine($"Next       {next}");
        }

        Console.WriteLine($"Folder     {job.Cwd}");
        if (job.Session is { } session)
        {
            Console.WriteLine($"Session    {session.Id}{(session.ShortId is { } shortId && shortId != session.Id ? $" (background {shortId})" : "")}");
            Console.WriteLine($"Attach     {Attach(job)}");
        }

        if (job.LastCheckpoint is { } checkpoint)
        {
            Console.WriteLine($"Checkpoint {checkpoint.At.ToLocalTime():yyyy-MM-dd HH:mm} {checkpoint.Branch}@{Short(checkpoint.Head)}{(checkpoint.Dirty ? " (uncommitted changes)" : "")}");
        }

        if (job.LastError is { } error && job.FailureCount > 0)
        {
            Console.WriteLine($"Retrying   {error} (attempt {job.FailureCount})");
        }

        Console.WriteLine($"Objective  {Cut(job.Objective.ReplaceLineEndings(" "), 200)}");
        if (events > 0)
        {
            var recent = store.ReadEvents(job.Id, events);
            if (recent.Count > 0)
            {
                Console.WriteLine(Format.Rule);
                foreach (var e in recent)
                {
                    Console.WriteLine($"{e.At.ToLocalTime():MM-dd HH:mm}  {e.Kind,-18} {e.Message}");
                }
            }
        }
    }

    private static string Attach(Job job) => job.Provider == JobProviderKind.Codex
        ? $"codex resume {job.Session!.Id}"
        : job.Session!.ShortId is { } shortId ? $"claude attach {shortId}" : $"claude --resume {job.Session.Id}";

    private static string Next(Job job, DateTimeOffset now) => job.Status switch
    {
        JobStatus.Scheduled => job.ResumeAt is { } at ? $"starts {When(at, now)}" : "starts now",
        JobStatus.WaitingQuota => job.ResumeAt is { } at ? $"resumes {When(at, now)}" : "when quota allows",
        JobStatus.Paused => job.ResumeMode switch
        {
            ResumeMode.At when job.ResumeAt is { } at => $"resumes {When(at, now)}",
            ResumeMode.QuotaAvailable => "when quota allows",
            _ => "fermata resume",
        },
        _ when job.NextAttemptAt is { } retry => $"retry {When(retry, now)}",
        _ => "",
    };

    private static string When(DateTimeOffset at, DateTimeOffset now) =>
        at <= now ? "now" : at - now < TimeSpan.FromHours(20) ? $"in {Format.Duration(at - now)}" : at.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture);

    internal static string StatusText(JobStatus status) => status switch
    {
        JobStatus.WaitingQuota => "waiting-quota",
        JobStatus.BlockedApproval => "needs-approval",
        JobStatus.BlockedUser => "needs-input",
        JobStatus.BlockedWorkspace => "workspace-changed",
        _ => status.ToString().ToLowerInvariant(),
    };

    internal static JsonObject ToJson(Job job) => new()
    {
        ["id"] = job.Id,
        ["name"] = job.Name,
        ["provider"] = job.Provider.ToString().ToLowerInvariant(),
        ["origin"] = job.Origin.ToString().ToLowerInvariant(),
        ["status"] = StatusText(job.Status),
        ["reason"] = job.BlockReason,
        ["resumeMode"] = job.ResumeMode.ToString().ToLowerInvariant(),
        ["resumeAt"] = job.ResumeAt,
        ["cwd"] = job.Cwd,
        ["sessionId"] = job.Session?.Id,
        ["backgroundId"] = job.Session?.ShortId,
        ["objective"] = job.Objective,
        ["lastCheckpoint"] = job.LastCheckpoint is { } c ? new JsonObject { ["at"] = c.At, ["branch"] = c.Branch, ["head"] = c.Head, ["dirty"] = c.Dirty, ["path"] = c.Path } : null,
        ["failureCount"] = job.FailureCount,
        ["lastError"] = job.LastError,
        ["createdAt"] = job.CreatedAt,
        ["updatedAt"] = job.UpdatedAt,
        ["finishedAt"] = job.FinishedAt,
    };

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Short(string? head) => head is { Length: > 8 } ? head[..8] : head ?? "?";
}
