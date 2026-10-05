using Fermata.Core.Jobs;
using Fermata.Core.Ports;

namespace Fermata.Platform.Jobs;

/// <summary>Desktop notification texts for job changes worth interrupting the user for.</summary>
public static class JobNotifications
{
    public static Notification For(JobNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var job = notice.Job;
        var title = notice.Event.Kind switch
        {
            "started" => $"Started: {job.DisplayName}",
            "resumed" => $"Resumed: {job.DisplayName}",
            "completed" => $"Done: {job.DisplayName}",
            "waiting" => $"Waiting for the {Provider(job)} limit: {job.DisplayName}",
            "stopping" => $"Stopping after this turn: {job.DisplayName}",
            "needs-approval" => $"Needs your approval: {job.DisplayName}",
            "needs-input" => $"Needs your answer: {job.DisplayName}",
            "workspace-changed" => $"Repository changed: {job.DisplayName}",
            "credit-suggested" => $"{Provider(job)} reset credit?",
            "failed" => $"Job failed: {job.DisplayName}",
            "paused" => $"Paused: {job.DisplayName}",
            "cancelled" => $"Cancelled: {job.DisplayName}",
            _ => job.DisplayName,
        };

        var kind = notice.Event.Kind switch
        {
            "waiting" or "stopping" => NotificationKind.LimitReached,
            "failed" or "workspace-changed" or "needs-approval" or "needs-input" => NotificationKind.ResetProblem,
            _ => NotificationKind.Info,
        };

        var attach = job.Session is null ? null
            : job.Provider == JobProviderKind.Codex ? $" Open it with `codex resume {job.Session.Id}`."
            : job.Session.ShortId is { } shortId ? $" Open it with `claude attach {shortId}`." : null;
        var body = notice.Event.Message
            + (notice.Event.Kind is "needs-approval" or "needs-input" && attach is not null && !notice.Event.Message.Contains("resume", StringComparison.Ordinal) && !notice.Event.Message.Contains("attach", StringComparison.Ordinal) ? attach : "");
        return new Notification(kind, title, body);
    }

    private static string Provider(Job job) => job.Provider == JobProviderKind.Codex ? "Codex" : "Claude";
}
