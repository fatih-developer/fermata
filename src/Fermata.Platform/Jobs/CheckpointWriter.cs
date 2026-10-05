using System.Globalization;
using System.Text.Json;
using Fermata.Core.Jobs;
using Fermata.Platform.Processes;

namespace Fermata.Platform.Jobs;

/// <summary>Git state of a working directory; null fields when it is not a repository.</summary>
public sealed record WorkspaceState(string? Branch, string? Head, string Status, string DiffStat)
{
    public bool IsRepository => Head is not null;

    public bool Dirty => Status.Length > 0;
}

/// <summary>One file in <c>jobs/&lt;id&gt;/checkpoints/</c>.</summary>
public sealed record CheckpointRecord(
    string JobId,
    DateTimeOffset At,
    string Reason,
    string Cwd,
    JobStatus Status,
    SessionRef? Session,
    string? Branch,
    string? Head,
    string GitStatus,
    string DiffStat,
    string? Patch,
    QuotaSnapshot? Quota,
    bool HandoffExists);

/// <summary>
/// Mechanical checkpoints without an LLM: branch, HEAD, <c>git status --porcelain</c>,
/// <c>git diff --stat</c> (and optionally the patch), the session and the quota.
/// </summary>
public sealed class CheckpointWriter
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

    private readonly IProcessRunner _runner;
    private readonly string? _git;

    public CheckpointWriter(IProcessRunner? runner = null, string? git = null)
    {
        _runner = runner ?? ProcessRunner.Instance;
        _git = git ?? _runner.FindOnPath("git");
    }

    public bool GitAvailable => _git is not null;

    public async Task<WorkspaceState> InspectAsync(string cwd, CancellationToken cancellationToken)
    {
        var head = await GitAsync(cwd, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
        if (head is null)
        {
            return new WorkspaceState(null, null, "", "");
        }

        var branch = await GitAsync(cwd, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        var status = await GitAsync(cwd, cancellationToken, "status", "--porcelain").ConfigureAwait(false) ?? "";
        var diffStat = await GitAsync(cwd, cancellationToken, "diff", "--stat", "HEAD").ConfigureAwait(false) ?? "";
        return new WorkspaceState(branch, head, status, diffStat);
    }

    /// <summary>Branch or HEAD moved since <paramref name="checkpoint"/> (uncommitted edits do not count).</summary>
    public async Task<bool> HasChangedAsync(string cwd, CheckpointInfo? checkpoint, CancellationToken cancellationToken)
    {
        if (checkpoint?.Head is null)
        {
            return false;
        }

        var now = await InspectAsync(cwd, cancellationToken).ConfigureAwait(false);
        return now.IsRepository && (now.Head != checkpoint.Head || now.Branch != checkpoint.Branch);
    }

    public async Task<CheckpointInfo> WriteAsync(Job job, string jobDirectory, string reason, bool savePatch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var state = await InspectAsync(job.Cwd, cancellationToken).ConfigureAwait(false);
        var patch = savePatch && state.IsRepository
            ? await GitAsync(job.Cwd, cancellationToken, "diff", "HEAD").ConfigureAwait(false)
            : null;

        var folder = Path.Combine(jobDirectory, "checkpoints");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".json");
        var record = new CheckpointRecord(
            job.Id,
            now,
            reason,
            job.Cwd,
            job.Status,
            job.Session,
            state.Branch,
            state.Head,
            state.Status,
            state.DiffStat,
            patch,
            job.LastQuota,
            File.Exists(Path.Combine(job.Cwd, JobPrompts.HandoffFile)));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, JobJsonContext.Default.CheckpointRecord), cancellationToken).ConfigureAwait(false);
        return new CheckpointInfo(now, path, state.Branch, state.Head, state.Dirty);
    }

    /// <summary>Adds <c>.fermata/</c> to the repository's <c>info/exclude</c> so handoff notes never get committed.</summary>
    public async Task EnsureExcludedAsync(string cwd, CancellationToken cancellationToken)
    {
        var exclude = await GitAsync(cwd, cancellationToken, "rev-parse", "--git-path", "info/exclude").ConfigureAwait(false);
        if (exclude is null)
        {
            return;
        }

        var path = Path.IsPathRooted(exclude) ? exclude : Path.Combine(cwd, exclude);
        const string Entry = ".fermata/";
        if (File.Exists(path) && File.ReadLines(path).Any(line => line.Trim() == Entry))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var prefix = File.Exists(path) && new FileInfo(path).Length > 0 && !File.ReadAllText(path).EndsWith('\n') ? "\n" : "";
        await File.AppendAllTextAsync(path, $"{prefix}# Fermata handoff notes\n{Entry}\n", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Trimmed stdout, or null when git is missing, fails or the folder is not a repository.</summary>
    private async Task<string?> GitAsync(string cwd, CancellationToken cancellationToken, params string[] args)
    {
        if (_git is null || !Directory.Exists(cwd))
        {
            return null;
        }

        try
        {
            var result = await _runner.RunAsync(_git, ["-C", cwd, .. args], GitTimeout, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? result.StandardOutput.TrimEnd() : null;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
