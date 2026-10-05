using System.Diagnostics;
using Fermata.Codex.AppServer;
using Fermata.Codex.JsonRpc;
using Fermata.Core.Jobs;
using Fermata.Core.Ports;

namespace Fermata.Codex.Jobs;

public sealed class CodexJobProviderOptions
{
    public required string CodexHome { get; init; }

    /// <summary>codex to run for <c>codex app-server daemon start</c>; null when not installed.</summary>
    public Func<string?> Executable { get; init; } = () => null;

    /// <summary>"user" or "auto_review" (Codex <c>approvalsReviewer</c>).</summary>
    public string Approvals { get; init; } = "user";

    public string Sandbox { get; init; } = "workspace-write";

    /// <summary>Model override for new threads; null keeps the user's Codex default.</summary>
    public string? Model { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Codex jobs are threads with a goal in the shared app-server daemon. Fermata starts the thread,
/// sets the goal and lets Codex run the turns; pausing and resuming flip the goal status. A goal
/// stopped as <c>usageLimited</c> is resumed once the monitor says usage is allowed again.
/// </summary>
public sealed class CodexJobProvider : IJobProvider
{
    private readonly CodexJobProviderOptions _options;
    private readonly Func<QuotaSnapshot?> _quota;
    private readonly Func<string, CancellationToken, Task<bool>> _startDaemon;

    /// <param name="quota">The monitor's latest fresh view (status.json), or null.</param>
    /// <param name="startDaemon">Runs <c>codex app-server daemon start</c>; tests replace it.</param>
    public CodexJobProvider(CodexJobProviderOptions options, Func<QuotaSnapshot?> quota, Func<string, CancellationToken, Task<bool>>? startDaemon = null)
    {
        _options = options;
        _quota = quota;
        _startDaemon = startDaemon ?? StartDaemonAsync;
    }

    public JobProviderKind Kind => JobProviderKind.Codex;

    public ResetCreditSupport ResetCredits => ResetCreditSupport.Api;

    public bool SupportsHandoff => false;

    /// <summary>Replaces the daemon connection (tests).</summary>
    internal Func<CancellationToken, Task<CodexDaemonClient>>? ConnectOverride { get; set; }

    public QuotaSnapshot? GetQuota() => _quota();

    public async Task<SessionRef> StartAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var client = await ConnectAsync(ensureDaemon: true, cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            return await Call(async () =>
            {
                var threadId = await client.StartThreadAsync(job.Cwd, _options.Sandbox, job.Approvals ?? _options.Approvals, _options.Model, cancellationToken).ConfigureAwait(false);
                await client.SetGoalAsync(threadId, job.Objective, "active", cancellationToken).ConfigureAwait(false);
                return new SessionRef(threadId);
            }).ConfigureAwait(false);
        }
    }

    public async Task<AdoptedSession> AdoptAsync(string? sessionId, string? objective, string cwd, CancellationToken cancellationToken)
    {
        var client = await ConnectAsync(ensureDaemon: false, cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            return await Call(async () =>
            {
                var loaded = await client.LoadedThreadsAsync(cancellationToken).ConfigureAwait(false);
                CodexThreadInfo thread;
                if (sessionId is null)
                {
                    var threads = new List<CodexThreadInfo>();
                    foreach (var id in loaded)
                    {
                        threads.Add(await client.ReadThreadAsync(id, cancellationToken).ConfigureAwait(false));
                    }

                    thread = threads.OrderByDescending(t => t.UpdatedAt ?? 0).FirstOrDefault()
                        ?? throw new JobProviderException("No Codex session is loaded in the shared daemon. Start Codex, or pass a thread id.", permanent: true);
                }
                else if (!loaded.Contains(sessionId))
                {
                    throw new JobProviderException($"Thread {sessionId} is not loaded in the shared Codex daemon. Open it in Codex first (`codex resume {sessionId}`).", permanent: true);
                }
                else
                {
                    thread = await client.ReadThreadAsync(sessionId, cancellationToken).ConfigureAwait(false);
                }

                var goal = await client.GetGoalAsync(thread.Id, cancellationToken).ConfigureAwait(false);
                if (goal is null || goal.Status == "complete")
                {
                    if (string.IsNullOrWhiteSpace(objective))
                    {
                        throw new JobProviderException(
                            $"Thread {thread.Id} has no goal. Pass --objective \"…\": Fermata continues Codex work only through goals, never by sending turns itself.",
                            permanent: true);
                    }

                    goal = await client.SetGoalAsync(thread.Id, objective, "active", cancellationToken).ConfigureAwait(false);
                }

                return new AdoptedSession(new SessionRef(thread.Id), thread.Cwd ?? cwd, goal?.Objective ?? objective ?? "");
            }).ConfigureAwait(false);
        }
    }

    public async Task<SessionObservation> GetSessionStateAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Session is not { } session)
        {
            return SessionObservation.None;
        }

        CodexDaemonClient client;
        try
        {
            client = await ConnectAsync(ensureDaemon: false, cancellationToken).ConfigureAwait(false);
        }
        catch (JobProviderException)
        {
            // No daemon: after a reboot or `daemon stop` nothing runs the goal any more.
            return new SessionObservation(SessionState.Interrupted, "the Codex daemon is not running");
        }

        await using (client.ConfigureAwait(false))
        {
            CodexThreadInfo thread;
            try
            {
                thread = await client.ReadThreadAsync(session.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonRpcException ex)
            {
                return new SessionObservation(SessionState.Gone, ex.Message);
            }

            var goal = await Call(() => client.GetGoalAsync(session.Id, cancellationToken)).ConfigureAwait(false);
            return Map(thread, goal);
        }
    }

    /// <summary>Thread status + goal → normalized session state.</summary>
    internal static SessionObservation Map(CodexThreadInfo thread, CodexGoal? goal)
    {
        var hint = $"open it with `codex resume {thread.Id}`";
        var turnRunning = thread.Status.Type == "active";
        if (turnRunning && thread.Status.WaitingOnApproval)
        {
            return new SessionObservation(SessionState.WaitingApproval, hint);
        }

        if (turnRunning && thread.Status.WaitingOnUserInput)
        {
            return new SessionObservation(SessionState.WaitingInput, hint);
        }

        return goal?.Status switch
        {
            null => new SessionObservation(SessionState.Paused, "the goal was cleared"),
            "complete" => new SessionObservation(SessionState.Completed),
            "usageLimited" => new SessionObservation(SessionState.LimitStopped),
            "budgetLimited" => new SessionObservation(SessionState.WaitingInput, $"the goal's token budget is used up; {hint}"),
            "blocked" => new SessionObservation(SessionState.WaitingInput, $"Codex marked the goal blocked; {hint}"),

            // Paused: the turn in flight still finishes; only then is the session really stopped.
            "paused" => new SessionObservation(turnRunning ? SessionState.Running : SessionState.Paused),
            _ => thread.Status.Type switch
            {
                "notLoaded" => new SessionObservation(SessionState.Interrupted, "the thread is no longer loaded in the Codex daemon"),
                "systemError" => new SessionObservation(SessionState.Interrupted, "Codex reported an error in this thread"),
                _ => new SessionObservation(SessionState.Running), // active, or idle between goal turns
            },
        };
    }

    public async Task<bool> PauseAsync(Job job, CancellationToken cancellationToken)
    {
        var threadId = ThreadOf(job);
        var client = await ConnectAsync(ensureDaemon: false, cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            return await Call(async () =>
            {
                await client.SetGoalAsync(threadId, null, "paused", cancellationToken).ConfigureAwait(false);
                var thread = await client.ReadThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
                return thread.Status.Type != "active";
            }).ConfigureAwait(false);
        }
    }

    /// <summary>Sets the goal active again; Codex drives the turns (the prompt is not needed).</summary>
    public async Task<SessionRef?> ResumeAsync(Job job, string prompt, CancellationToken cancellationToken)
    {
        var threadId = ThreadOf(job);
        var client = await ConnectAsync(ensureDaemon: true, cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            await Call(async () =>
            {
                var thread = await client.ReadThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
                if (thread.Status.Type == "notLoaded")
                {
                    await client.LoadThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
                }

                var goal = await client.GetGoalAsync(threadId, cancellationToken).ConfigureAwait(false);
                await client.SetGoalAsync(threadId, goal is null ? job.Objective : null, "active", cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }

        return null;
    }

    public Task<bool> RequestHandoffAsync(Job job, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>Pauses the goal; the thread stays for the user.</summary>
    public async Task CancelAsync(Job job, CancellationToken cancellationToken)
    {
        if (job?.Session is null)
        {
            return;
        }

        try
        {
            await PauseAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (JobProviderException)
        {
            // Cancelling must work even with the daemon gone.
        }
    }

    private static string ThreadOf(Job job) =>
        job?.Session?.Id ?? throw new JobProviderException("The job has no Codex thread yet.", permanent: true);

    private async Task<CodexDaemonClient> ConnectAsync(bool ensureDaemon, CancellationToken cancellationToken)
    {
        var connect = ConnectOverride ?? (ct => CodexDaemonClient.ConnectAsync(_options.CodexHome, _options.Timeout, ct));

        try
        {
            return await connect(cancellationToken).ConfigureAwait(false);
        }
        catch (CodexUnavailableException ex) when (ensureDaemon)
        {
            var codex = _options.Executable()
                ?? throw new JobProviderException("codex was not found. Install Codex or set [codex] executable.", ex, permanent: true);
            if (!await _startDaemon(codex, cancellationToken).ConfigureAwait(false))
            {
                throw new JobProviderException("`codex app-server daemon start` failed.", ex);
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await connect(cancellationToken).ConfigureAwait(false);
                }
                catch (CodexUnavailableException) when (attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (CodexUnavailableException retry)
                {
                    throw new JobProviderException($"The Codex daemon did not come up: {retry.Message}", retry);
                }
            }
        }
        catch (CodexUnavailableException ex)
        {
            throw new JobProviderException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is CodexTransientException or JsonRpcException or CodexProtocolException)
        {
            throw new JobProviderException($"Could not talk to the Codex daemon: {ex.Message}", ex);
        }
    }

    /// <summary>Maps protocol failures to retryable provider errors.</summary>
    private static async Task<T> Call<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CodexTransientException or CodexProtocolException)
        {
            throw new JobProviderException($"Codex daemon: {ex.Message}", ex);
        }
        catch (JsonRpcException ex)
        {
            throw new JobProviderException($"Codex daemon: {ex.Message}", ex);
        }
    }

    private static async Task<bool> StartDaemonAsync(string codex, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(codex)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "app-server", "daemon", "start" })
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }
}
