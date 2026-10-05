using System.Diagnostics;
using Fermata.Claude;
using Fermata.Core.Jobs;
using Fermata.Core.Policies;
using Fermata.Core.Ports;
using Fermata.Platform;
using Fermata.Platform.Jobs;
using Microsoft.Extensions.Logging;

namespace Fermata.Desktop.Services;

/// <summary>Supervised jobs in the tray app: the scheduler, the job rows and the Claude Code integration.</summary>
public sealed partial class DesktopHost
{
    private static readonly TimeSpan JobsRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly Func<GuardOptions, IReadOnlyList<IJobProvider>>? _jobProviders;
    private readonly JsonJobStore _jobStore;
    private CancellationTokenSource? _jobsStop;
    private Task _jobsTask = Task.CompletedTask;

    /// <summary>Null when the host was built without providers (tests).</summary>
    internal JobScheduler? Scheduler { get; private set; }

    /// <summary>Claude Code settings folder the settings panel edits; tests point it elsewhere.</summary>
    internal string ClaudeHome { get; set; } = ClaudeSettingsInstaller.DefaultClaudeHome();

    /// <summary>Opens a URL in the browser; tests replace it.</summary>
    internal Action<string> OpenUrl { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    private void StartJobs()
    {
        if (_jobProviders is null)
        {
            return;
        }

        var scheduler = new JobScheduler(_jobStore, _jobProviders(Options), new CheckpointWriter(), () => Options.Jobs, _time, _loggers.CreateLogger<JobScheduler>());
        scheduler.Changed += OnJobChanged;
        Scheduler = scheduler;
        _jobsStop = new CancellationTokenSource();
        var token = _jobsStop.Token;
        var jobsLock = new FileResetLock(_paths.JobsLockFile);
        _jobsTask = Task.WhenAll(
            Task.Run(() => scheduler.RunAsync(jobsLock, token), CancellationToken.None),
            Task.Run(() => RefreshJobsLoopAsync(token), CancellationToken.None));
    }

    private async Task StopJobsAsync()
    {
        if (_jobsStop is null)
        {
            return;
        }

        await _jobsStop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _jobsTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }

        _jobsStop.Dispose();
        _jobsStop = null;
    }

    private async Task RefreshJobsLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(JobsRefreshInterval, _time, cancellationToken).ConfigureAwait(false);
                _ui?.Post(RefreshJobs);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnJobChanged(JobNotice notice)
    {
        _ui?.Post(() =>
        {
            RefreshJobs();
            if (notice.Important)
            {
                ViewModel.AddEvent($"{notice.Job.DisplayName}: {notice.Event.Message}", notice.Event.At);
            }
        });

        if (notice.Important && _notifier.IsAvailable)
        {
            _ = _notifier.ShowAsync(JobNotifications.For(notice), CancellationToken.None);
        }
    }

    internal void RefreshJobs()
    {
        try
        {
            ViewModel.ApplyJobs(_jobStore.List(), id => _jobStore.ReadEvents(id, 5), _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogJobsReadFailed(_logger, ex);
        }
    }

    /// <summary>Requests go through the job folder, so they reach whichever process runs the scheduler.</summary>
    internal async Task PauseJobAsync(string id)
    {
        _jobStore.Enqueue(new JobRequest(id, JobRequestKind.Pause, _time.GetUtcNow()));
        await AfterRequestAsync().ConfigureAwait(true);
    }

    /// <summary>"Resume" in the UI means now (e.g. after a reset credit); the workspace check still applies.</summary>
    internal async Task ResumeJobAsync(string id)
    {
        _jobStore.Enqueue(new JobRequest(id, JobRequestKind.Resume, _time.GetUtcNow(), Now: true));
        await AfterRequestAsync().ConfigureAwait(true);
    }

    private async Task AfterRequestAsync()
    {
        if (Scheduler is { IsActive: true } scheduler)
        {
            try
            {
                await scheduler.TickAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or JobProviderException)
            {
                LogJobsReadFailed(_logger, ex);
            }
        }
        else
        {
            // Another process (`fermata daemon`) runs the scheduler; it polls for requests every 2 s.
            await Task.Delay(TimeSpan.FromSeconds(2.5), _time).ConfigureAwait(true);
        }

        RefreshJobs();
    }

    private void OpenClaudeLimitResets()
    {
        try
        {
            OpenUrl(Options.Jobs.Claude.LimitResetsUrl);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ViewModel.AddEvent($"Could not open {Options.Jobs.Claude.LimitResetsUrl}: {ex.Message}", _time.GetUtcNow());
        }
    }

    private ClaudeSettingsInstaller ClaudeInstaller() => new(ClaudeHome, new ClaudeStateStore(_paths));

    internal void RefreshClaudeIntegration()
    {
        try
        {
            var status = ClaudeInstaller().GetStatus();
            ViewModel.ClaudeIntegrationInstalled = status.Installed;
            ViewModel.ClaudeIntegrationText = !status.Installed
                ? "Not added. Fermata then sees Claude's quota and prompts, and can resume Claude jobs."
                : !status.ExecutableExists
                    ? "Added, but it points to a missing fermata. Add it again."
                    : "Added. Restart running Claude Code sessions to load the hooks.";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ViewModel.ClaudeIntegrationText = $"Cannot read Claude Code settings: {ex.Message}";
        }
    }

    internal async Task ToggleClaudeIntegrationAsync()
    {
        var cli = new ClaudeCli(() => ClaudeLocator.Resolve(Options.Jobs.Claude.Executable));
        var installer = ClaudeInstaller();
        try
        {
            if (ViewModel.ClaudeIntegrationInstalled)
            {
                installer.Uninstall();
                await TryMcpAsync(() => cli.RemoveMcpServerAsync(CancellationToken.None)).ConfigureAwait(true);
                ViewModel.AddEvent("Removed Fermata from Claude Code.", _time.GetUtcNow());
            }
            else if (!File.Exists(CliExecutable))
            {
                ViewModel.ClaudeIntegrationText = $"fermata was not found next to the app ({CliExecutable}).";
                return;
            }
            else
            {
                installer.Install(CliExecutable);
                if (!await TryMcpAsync(() => cli.AddMcpServerAsync(CliExecutable, CancellationToken.None)).ConfigureAwait(true))
                {
                    ViewModel.AddEvent("The Fermata MCP server was not registered in Claude Code (claude not found).", _time.GetUtcNow());
                }
                ViewModel.AddEvent($"Added Fermata to Claude Code ({installer.SettingsFile}).", _time.GetUtcNow());
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ViewModel.ClaudeIntegrationText = $"Could not update Claude Code settings: {ex.Message}";
            return;
        }

        RefreshClaudeIntegration();
    }

    /// <summary>Claude's own MCP registration is a bonus: a missing claude never fails the toggle.</summary>
    private static async Task<bool> TryMcpAsync(Func<Task<bool>> action)
    {
        try
        {
            return await action().ConfigureAwait(true);
        }
        catch (JobProviderException)
        {
            return false;
        }
    }

    private IReadOnlySet<string> JobThreads() =>
        _jobStore.List()
            .Where(j => !j.IsFinished && j.Provider == JobProviderKind.Codex && j.Session is not null)
            .Select(j => j.Session!.Id)
            .ToHashSet();

    [LoggerMessage(410, LogLevel.Warning, "Reading or driving jobs failed")]
    private static partial void LogJobsReadFailed(ILogger logger, Exception error);
}
