using System.Globalization;
using Microsoft.Extensions.Logging;
using Fermata.Cli.Output;
using Fermata.Codex.AppServer;
using Fermata.Core.Domain;
using Fermata.Core.Monitoring;
using Fermata.Core.Policies;
using Fermata.Core.Reset;
using Fermata.Platform;
using Fermata.Platform.Jobs;
using Fermata.Platform.Notifications;

namespace Fermata.Cli.Commands;

/// <summary>
/// `fermata daemon`: headless monitor for login services (systemd --user, LaunchAgent) and
/// SSH/server use (PRD §23, §25). Never prompts; confirm mode reports via notification and log.
/// Output is one plain line per event so journald/launchd logs stay readable.
/// </summary>
internal static class DaemonCommand
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var runtime = GuardRuntime.Load();
        var options = runtime.Options;
        var notifier = NotifierFactory.Create(options.NotificationsEnabled);
        var timing = MonitorTiming.FromOptions(options);

        Console.WriteLine($"fermata daemon: mode {Format.Mode(options.Mode)}, poll {timing.PollInterval.TotalSeconds:0}s, notifications {notifier.Mechanism}");

        var monitor = new RateLimitMonitor(
            new CodexAppServerConnector(
                new CodexClientOptions { Executable = options.CodexExecutable },
                TimeProvider.System,
                AppLogging.Factory.CreateLogger("Fermata.Codex")),
            client => new ResetManager(client, runtime.StateStore, runtime.Lock, options, TimeProvider.System, AppLogging.Factory.CreateLogger<ResetManager>()),
            runtime.StateStore,
            new SnapshotObserver(
                new ContinuingObserver(
                    new NotifyingObserver(new DaemonObserver(), notifier, TimeProvider.System, NotificationTexts.CommandAskHint),
                    options,
                    notifier,
                    text => Console.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} CONTINUE {text}")),
                new JsonStatusSnapshotStore(runtime.Paths.StatusFile),
                options,
                "daemon",
                TimeProvider.System),
            options,
            timing,
            TimeProvider.System,
            AppLogging.Factory.CreateLogger<RateLimitMonitor>());

        var jobs = JobRuntime.Create(runtime.Paths, options);
        var scheduler = jobs.CreateScheduler();
        scheduler.Changed += notice =>
        {
            Console.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} JOB {notice.Job.Id} {notice.Event.Kind}: {notice.Event.Message}");
            if (notice.Important && notifier.IsAvailable)
            {
                _ = notifier.ShowAsync(JobNotifications.For(notice), CancellationToken.None);
            }
        };

        // The monitor and the job scheduler run side by side; either stops when the daemon does.
        await Task.WhenAll(
            monitor.RunAsync(cancellationToken),
            scheduler.RunAsync(jobs.Lock, cancellationToken)).ConfigureAwait(false);
        Console.WriteLine("fermata daemon: stopped");
        return ExitCodes.Ok;
    }
}

internal sealed class DaemonObserver : IMonitorObserver
{
    private string? _lastLine;
    private bool _outage;

    public bool CanConfirm => false;

    private static string Stamp => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public void OnUsage(CodexUsage usage, LimitAssessment assessment)
    {
        _outage = false;
        var line = $"5h {Format.Percent(usage.FiveHour).Trim()} weekly {Format.Percent(usage.Weekly).Trim()} resets {usage.AvailableResetCount} status {Format.StatusCode(assessment, usage)}";
        if (line != _lastLine)
        {
            Console.WriteLine($"{Stamp} {line}");
            _lastLine = line;
        }
    }

    public void OnLimitReached(LimitNotice notice)
    {
        var text = NotificationTexts.ForLimit(notice, DateTimeOffset.UtcNow, NotificationTexts.CommandAskHint);
        Console.WriteLine($"{Stamp} LIMIT {text.Title}: {text.Body}");
    }

    public void OnPendingAttempt(PendingResetAttempt pending) =>
        Console.WriteLine($"{Stamp} PENDING attempt {pending.IdempotencyKey} from {pending.StartedAt:o}: run `fermata reset` to finish it");

    public void OnNearLimit(NearLimitNotice notice, CodexUsage usage)
    {
        var text = NotificationTexts.ForNearLimit(notice, usage.AvailableResetCount, DateTimeOffset.UtcNow);
        Console.WriteLine($"{Stamp} NEAR_LIMIT {text.Title}: {text.Body}");
    }

    public Task<bool> ConfirmResetAsync(LimitNotice notice, CancellationToken cancellationToken) => Task.FromResult(false);

    public void OnResetCompleted(ResetReport report)
    {
        var text = NotificationTexts.ForReport(report);
        Console.WriteLine($"{Stamp} RESET {report.Status}: {text.Body}");
        _lastLine = null;
    }

    public void OnResetFailed(Exception error) => Console.WriteLine($"{Stamp} RESET failed: {error.Message}");

    public void OnAuthRequired() => Console.WriteLine($"{Stamp} AUTH_REQUIRED: run `codex login`");

    public void OnUnavailable(Exception error, TimeSpan retryIn)
    {
        if (!_outage)
        {
            Console.WriteLine($"{Stamp} CODEX_UNAVAILABLE: {error.Message}");
            _outage = true;
        }
    }
}
