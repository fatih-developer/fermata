using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Fermata.Core.Jobs;
using Fermata.Core.Monitoring;

namespace Fermata.Desktop.ViewModels;

/// <summary>One supervised job in the flyout and the Details window.</summary>
public sealed partial class JobRowViewModel
{
    private readonly Func<string, Task> _pause;
    private readonly Func<string, Task> _resume;

    public JobRowViewModel(Job job, IReadOnlyList<JobEvent> events, DateTimeOffset now, TimeZoneInfo zone, Func<string, Task> pause, Func<string, Task> resume)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(events);
        _pause = pause;
        _resume = resume;
        Id = job.Id;
        Name = job.DisplayName;
        Provider = job.Provider == JobProviderKind.Codex ? "Codex" : "Claude";
        Status = StatusName(job.Status);
        Next = NextText(job, now, zone);
        Reason = job.BlockReason ?? job.LastError ?? "";
        Objective = job.Objective.ReplaceLineEndings(" ");
        Attach = job.Session is null ? ""
            : job.Provider == JobProviderKind.Codex ? $"codex resume {job.Session.Id}"
            : job.Session.ShortId is { } shortId ? $"claude attach {shortId}" : $"claude --resume {job.Session.Id}";
        CanPause = job.Status is JobStatus.Running or JobStatus.BlockedApproval or JobStatus.BlockedUser or JobStatus.Scheduled;
        CanResume = job.Status is JobStatus.Paused or JobStatus.BlockedWorkspace or JobStatus.WaitingQuota;
        NeedsAttention = job.Status is JobStatus.BlockedApproval or JobStatus.BlockedUser or JobStatus.BlockedWorkspace or JobStatus.Failed;
        IsWaiting = job.Status == JobStatus.WaitingQuota;
        IsClaudeWaiting = IsWaiting && job.Provider == JobProviderKind.Claude;
        Events = events.Reverse().Take(5).Select(e => $"{TimeZoneInfo.ConvertTime(e.At, zone):HH:mm}  {e.Message}").ToList();
    }

    public string Id { get; }

    public string Name { get; }

    public string Provider { get; }

    public string Status { get; }

    /// <summary>"resumes in 2h 10m", "starts 07:30"…</summary>
    public string Next { get; }

    public string Reason { get; }

    public string Objective { get; }

    /// <summary>How to open the session yourself.</summary>
    public string Attach { get; }

    public bool CanPause { get; }

    public bool CanResume { get; }

    public bool NeedsAttention { get; }

    public bool IsWaiting { get; }

    public bool IsClaudeWaiting { get; }

    public IReadOnlyList<string> Events { get; }

    /// <summary>"Claude · waiting-quota · resumes in 2h".</summary>
    public string Summary => string.Join(" · ", new[] { Provider, Status, Next }.Where(s => s.Length > 0));

    [RelayCommand]
    private Task PauseAsync() => _pause(Id);

    [RelayCommand]
    private Task ResumeAsync() => _resume(Id);

    private static string StatusName(JobStatus status) => status switch
    {
        JobStatus.WaitingQuota => "waiting for quota",
        JobStatus.BlockedApproval => "needs approval",
        JobStatus.BlockedUser => "needs your answer",
        JobStatus.BlockedWorkspace => "repository changed",
        JobStatus.Checkpointing => "stopping after this turn",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static string NextText(Job job, DateTimeOffset now, TimeZoneInfo zone)
    {
        string When(DateTimeOffset at) => at <= now
            ? "now"
            : at - now < TimeSpan.FromHours(20)
                ? $"in {NotificationTexts.Duration(at - now)}"
                : TimeZoneInfo.ConvertTime(at, zone).ToString("ddd HH:mm", CultureInfo.InvariantCulture);

        return job.Status switch
        {
            JobStatus.Scheduled => job.ResumeAt is { } at ? $"starts {When(at)}" : "starting",
            JobStatus.WaitingQuota => job.ResumeAt is { } at ? $"resumes {When(at)}" : "resumes when quota allows",
            JobStatus.Paused when job.ResumeMode == ResumeMode.At && job.ResumeAt is { } at => $"resumes {When(at)}",
            _ when job.NextAttemptAt is { } retry => $"retry {When(retry)}",
            _ => "",
        };
    }
}
