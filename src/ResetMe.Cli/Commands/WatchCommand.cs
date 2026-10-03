using Microsoft.Extensions.Logging;
using System.Globalization;
using ResetMe.Cli.Output;
using ResetMe.Codex.AppServer;
using ResetMe.Core.Domain;
using ResetMe.Core.Monitoring;
using ResetMe.Core.Policies;
using ResetMe.Core.Reset;
using ResetMe.Platform.Notifications;

namespace ResetMe.Cli.Commands;

/// <summary>`resetme watch`: foreground monitor with the confirm prompt (PRD §36, §52).</summary>
internal static class WatchCommand
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var runtime = GuardRuntime.Load();
        var options = runtime.Options;
        var timing = MonitorTiming.FromOptions(options);

        Console.WriteLine($"Watching Codex usage — mode {Format.Mode(options.Mode)}, every {timing.PollInterval.TotalSeconds:0}s. Ctrl+C to stop.");
        if (options.Mode == GuardMode.Automatic)
        {
            Console.WriteLine($"Automatic mode: credits are redeemed without asking (max {options.Automatic.MaxResetsPerDay}/day, {options.Automatic.MaxResetsPerWeek}/week).");
        }

        foreach (var warning in runtime.Config.Warnings)
        {
            Console.WriteLine($"config warning: {warning}");
        }

        Console.WriteLine();

        var connector = new CodexAppServerConnector(
            new CodexClientOptions { Executable = options.CodexExecutable },
            TimeProvider.System,
            AppLogging.Factory.CreateLogger("ResetMe.Codex"));
        var monitor = new RateLimitMonitor(
            connector,
            client => new ResetManager(client, runtime.StateStore, runtime.Lock, options, TimeProvider.System, AppLogging.Factory.CreateLogger<ResetManager>()),
            runtime.StateStore,
            new NotifyingObserver(
                new ConsoleWatchObserver(options),
                NotifierFactory.Create(options.NotificationsEnabled),
                TimeProvider.System,
                NotificationTexts.TerminalAskHint),
            options,
            timing,
            TimeProvider.System,
            AppLogging.Factory.CreateLogger<RateLimitMonitor>());

        await monitor.RunAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine("Stopped.");
        return ExitCodes.Ok;
    }
}

/// <summary>Terminal presentation of monitor events. Prints a usage line only when something changed.</summary>
internal sealed class ConsoleWatchObserver : IMonitorObserver
{
    private readonly GuardOptions _options;
    private string? _lastLine;
    private bool? _lastBlocked;
    private bool _outage;
    private bool _authMissing;

    public ConsoleWatchObserver(GuardOptions options)
    {
        _options = options;
    }

    private static string Clock => DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);

    public void OnUsage(CodexUsage usage, LimitAssessment assessment)
    {
        if (_outage || _authMissing)
        {
            Console.WriteLine($"{Clock}  Connected to Codex again.");
            _outage = false;
            _authMissing = false;
        }

        var line = $"5h {Format.Percent(usage.FiveHour)}  weekly {Format.Percent(usage.Weekly)}  resets {usage.AvailableResetCount.ToString(CultureInfo.InvariantCulture)}";
        if (usage.UsageAllowed is null)
        {
            line += "  (usage flag not reported)";
        }

        if (line != _lastLine)
        {
            Console.WriteLine($"{Clock}  {line}");
            _lastLine = line;
        }

        if (_lastBlocked == true && !assessment.Blocked)
        {
            Console.WriteLine($"{Clock}  Codex is usable again.");
        }

        _lastBlocked = assessment.Blocked;
    }

    public void OnLimitReached(LimitNotice notice)
    {
        var now = DateTimeOffset.UtcNow;
        Console.WriteLine();
        Console.Write('\a');

        switch (notice.Handling)
        {
            case LimitHandling.AskUser:
                ResetPresenter.PrintOffer(notice.Usage, notice.Assessment, now);
                break;

            case LimitHandling.ResetAutomatically:
                ResetPresenter.PrintOffer(notice.Usage, notice.Assessment, now);
                Console.WriteLine("Automatic mode: attempting a reset (daily/weekly caps and cooldown apply)…");
                break;

            case LimitHandling.ReportOnly:
                ResetPresenter.PrintOffer(notice.Usage, notice.Assessment, now);
                Console.WriteLine(_options.Mode == GuardMode.Manual
                    ? "Manual mode: run `resetme reset` to use a credit."
                    : "No interactive terminal to ask; run `resetme reset` to use a credit.");
                Console.WriteLine();
                break;

            case LimitHandling.NotOffered when notice.Assessment.NoOfferReason is { } reason:
                Console.WriteLine("Codex limit reached.");
                Console.WriteLine(ResetPresenter.Explain(reason, notice.Usage, notice.Assessment, _options, now));
                Console.WriteLine();
                break;

            case LimitHandling.AlreadyHandled:
                Console.WriteLine("Codex limit reached in an episode that already had a reset attempt; not offering another.");
                Console.WriteLine("Use `resetme reset --force` if you are sure.");
                Console.WriteLine();
                break;
        }
    }

    /// <summary>Without an interactive stdin confirm mode only reports (and suggests `resetme reset`).</summary>
    public bool CanConfirm => !Console.IsInputRedirected;

    public void OnPendingAttempt(PendingResetAttempt pending) =>
        Console.WriteLine($"{Clock}  PENDING: a reset attempt from {pending.StartedAt.ToLocalTime():g} has no confirmed result. Run `resetme reset` to finish it (same key, no second credit).");

    public void OnNearLimit(NearLimitNotice notice, CodexUsage usage)
    {
        var text = NotificationTexts.ForNearLimit(notice, usage.AvailableResetCount, DateTimeOffset.UtcNow);
        Console.WriteLine($"{Clock}  NEAR LIMIT: {text.Title}. {text.Body}");
    }

    public async Task<bool> ConfirmResetAsync(LimitNotice notice, CancellationToken cancellationToken)
    {
        Console.Write("Use reset credit? [y/N] ");
        var answer = (await Task.Run(Console.ReadLine, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false))
            ?.Trim().ToLowerInvariant();

        if (answer is "y" or "yes" or "e" or "evet")
        {
            return true;
        }

        Console.WriteLine("Waiting. Not asking again for this limit.");
        Console.WriteLine();
        return false;
    }

    public void OnResetCompleted(ResetReport report)
    {
        ResetPresenter.PrintReport(report, _options, DateTimeOffset.UtcNow);
        Console.WriteLine("Monitoring continues.");
        Console.WriteLine();
        _lastLine = null; // Always show the post-reset usage line.
        if (report.Status == ResetRunStatus.Succeeded)
        {
            _lastBlocked = false; // The report already said Codex is usable again.
        }
    }

    public void OnResetFailed(Exception error)
    {
        Console.WriteLine($"Reset failed: {error.Message}");
        Console.WriteLine("If an attempt was started, `resetme reset` finishes it with the same idempotency key.");
        Console.WriteLine();
    }

    public void OnAuthRequired()
    {
        if (!_authMissing)
        {
            Console.WriteLine($"{Clock}  AUTH_REQUIRED: Codex is not logged in. Run `codex login`; watching continues.");
            _authMissing = true;
        }
    }

    public void OnUnavailable(Exception error, TimeSpan retryIn)
    {
        if (!_outage)
        {
            Console.WriteLine($"{Clock}  CODEX_UNAVAILABLE: {error.Message} Retrying…");
            _outage = true;
        }
    }
}
