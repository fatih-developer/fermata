using Fermata.Codex.AppServer;
using Fermata.Core.Domain;
using Fermata.Core.Monitoring;
using Fermata.Core.Policies;
using Fermata.Core.Ports;
using Fermata.Core.Reset;
using Fermata.Platform.Codex;
using Fermata.Core.Jobs;
using Fermata.Platform;
using Fermata.Platform.Jobs;

namespace Fermata.Cli;

/// <summary>Continues the user's Codex work after a successful reset (<c>[codex] continue_after_reset</c>).</summary>
internal static class SessionContinuation
{
    /// <summary>The line to show, or null when disabled or nothing was waiting.</summary>
    public static async Task<string?> RunAsync(GuardOptions options, CancellationToken cancellationToken)
    {
        if (!options.ContinueAfterReset)
        {
            return null;
        }

        var report = await new CodexSessionResumer().ResumeAsync(CodexHooksInstaller.DefaultCodexHome(), cancellationToken, skipThreads: JobThreads()).ConfigureAwait(false);
        return report.Describe();
    }

    /// <summary>Codex threads that belong to open jobs: the job scheduler resumes those (after its workspace check).</summary>
    public static IReadOnlySet<string> JobThreads() =>
        new JsonJobStore(AppPaths.Default().JobsDirectory).List()
            .Where(j => !j.IsFinished && j.Provider == JobProviderKind.Codex && j.Session is not null)
            .Select(j => j.Session!.Id)
            .ToHashSet();
}

/// <summary>Runs <see cref="SessionContinuation"/> after every successful monitor reset (daemon, watch).</summary>
internal sealed class ContinuingObserver(IMonitorObserver inner, GuardOptions options, INotifier notifier, Action<string> print) : IMonitorObserver
{
    public bool CanConfirm => inner.CanConfirm;

    public void OnUsage(CodexUsage usage, LimitAssessment assessment) => inner.OnUsage(usage, assessment);

    public void OnLimitReached(LimitNotice notice) => inner.OnLimitReached(notice);

    public void OnPendingAttempt(PendingResetAttempt pending) => inner.OnPendingAttempt(pending);

    public void OnNearLimit(NearLimitNotice notice, CodexUsage usage) => inner.OnNearLimit(notice, usage);

    public Task<bool> ConfirmResetAsync(LimitNotice notice, CancellationToken cancellationToken) =>
        inner.ConfirmResetAsync(notice, cancellationToken);

    public void OnResetCompleted(ResetReport report)
    {
        inner.OnResetCompleted(report);
        if (report.Status == ResetRunStatus.Succeeded)
        {
            _ = Task.Run(ContinueAsync);
        }
    }

    public void OnResetFailed(Exception error) => inner.OnResetFailed(error);

    public void OnAuthRequired() => inner.OnAuthRequired();

    public void OnUnavailable(Exception error, TimeSpan retryIn) => inner.OnUnavailable(error, retryIn);

    private async Task ContinueAsync()
    {
        var text = await SessionContinuation.RunAsync(options, CancellationToken.None).ConfigureAwait(false);
        if (text is null)
        {
            return;
        }

        print(text);
        if (notifier.IsAvailable)
        {
            await notifier.ShowAsync(new Notification(NotificationKind.Info, "Codex work continues", text), CancellationToken.None).ConfigureAwait(false);
        }
    }
}
