namespace Fermata.Core.Jobs;

/// <summary>
/// Jobs on disk. Only the process holding the scheduler lock writes an existing job; others create
/// new jobs and send <see cref="JobRequest"/>s.
/// </summary>
public interface IJobStore
{
    IReadOnlyList<Job> List();

    Job? Get(string id);

    bool Exists(string id);

    /// <summary>Must be atomic: a crash mid-write never leaves a half-written job.</summary>
    void Save(Job job);

    void AppendEvent(string id, JobEvent jobEvent);

    IReadOnlyList<JobEvent> ReadEvents(string id, int tail);

    /// <summary>Folder for the job's files (checkpoints, markers).</summary>
    string DirectoryOf(string id);

    void Enqueue(JobRequest request);

    /// <summary>Pending requests, oldest first; each is removed once returned.</summary>
    IReadOnlyList<JobRequest> TakeRequests();

    bool HasRequests();
}

public enum JobRequestKind
{
    /// <summary>A job another process created: take it over and make its first step.</summary>
    Start,
    Pause,
    Resume,
    Cancel,
    Checkpoint,
}

/// <summary>A user command for a job, carried out by the scheduler.</summary>
/// <param name="Mode">Resume: when to continue. Pause: Manual, or At with <paramref name="At"/>.</param>
/// <param name="Now">Resume immediately, even if the last known quota says the limit is on.</param>
/// <param name="Force">Resume even though the workspace changed since the checkpoint.</param>
public sealed record JobRequest(
    string JobId,
    JobRequestKind Kind,
    DateTimeOffset RequestedAt,
    ResumeMode Mode = ResumeMode.QuotaAvailable,
    DateTimeOffset? At = null,
    bool Now = false,
    bool Force = false)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}
