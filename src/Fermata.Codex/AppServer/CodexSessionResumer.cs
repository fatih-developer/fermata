using Fermata.Codex.JsonRpc;
using Fermata.Core.Ports;

namespace Fermata.Codex.AppServer;

/// <summary>What happened to the user's Codex sessions after a reset.</summary>
/// <param name="DaemonReachable">False when no shared Codex daemon runs (no live sessions to resume).</param>
/// <param name="GoalsResumed">Threads whose usage-limited goal was set back to active.</param>
/// <param name="StoppedTurns">Threads without a goal whose last turn failed at the usage limit; the user sends "continue".</param>
public sealed record SessionResumeReport(
    bool DaemonReachable,
    IReadOnlyList<string> GoalsResumed,
    IReadOnlyList<string> StoppedTurns,
    string? Error = null,
    int LiveSessions = 0)
{
    public static readonly SessionResumeReport NoDaemon = new(false, [], []);

    /// <summary>One line for events and notifications; null when there is nothing to tell.</summary>
    public string? Describe()
    {
        var parts = new List<string>();
        if (GoalsResumed.Count > 0)
        {
            parts.Add(GoalsResumed.Count == 1
                ? "Resumed 1 Codex goal that stopped at the limit."
                : $"Resumed {GoalsResumed.Count} Codex goals that stopped at the limit.");
        }

        if (StoppedTurns.Count > 0)
        {
            parts.Add((StoppedTurns.Count == 1 ? "1 Codex session" : $"{StoppedTurns.Count} Codex sessions")
                + " stopped at the limit: send \"continue\" there. Tip: start long tasks with /goal and Fermata resumes them for you.");
        }

        if (Error is not null)
        {
            parts.Add($"Could not check every Codex session: {Error}");
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}

/// <summary>
/// After a reset, continues unattended work in the user's live Codex sessions (the shared
/// app-server daemon). Uses only the stable goal API: a goal stopped as <c>usageLimited</c> is set
/// back to <c>active</c>, and Codex itself drives the next turns (its approvals still go to the
/// user's client). Plain turns are never started on the user's behalf; they are only reported.
/// Threads that belong to Fermata jobs are left to the job scheduler (it checks the workspace first).
/// </summary>
public sealed class CodexSessionResumer
{
    internal const string UsageLimited = "usageLimited";

    private readonly TimeSpan _timeout;

    public CodexSessionResumer(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <param name="dryRun">Only inspect: report what would be resumed, change nothing.</param>
    /// <param name="skipThreads">Thread ids owned by Fermata jobs.</param>
    public async Task<SessionResumeReport> ResumeAsync(string codexHome, CancellationToken cancellationToken, bool dryRun = false, IReadOnlySet<string>? skipThreads = null)
    {
        CodexDaemonClient client;
        try
        {
            client = await CodexDaemonClient.ConnectAsync(codexHome, _timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CodexUnavailableException or CodexTransientException or JsonRpcException)
        {
            return SessionResumeReport.NoDaemon with { Error = ex.Message };
        }

        await using (client.ConfigureAwait(false))
        {
            return await ResumeAsync(client, cancellationToken, dryRun, skipThreads).ConfigureAwait(false);
        }
    }

    /// <summary>Over an already connected stream (tests).</summary>
    internal async Task<SessionResumeReport> ResumeAsync(JsonRpcConnection connection, CancellationToken cancellationToken, bool dryRun = false, IReadOnlySet<string>? skipThreads = null)
    {
        var client = await CodexDaemonClient.OverAsync(connection, _timeout, cancellationToken).ConfigureAwait(false);
        return await ResumeAsync(client, cancellationToken, dryRun, skipThreads).ConfigureAwait(false);
    }

    private static async Task<SessionResumeReport> ResumeAsync(CodexDaemonClient client, CancellationToken cancellationToken, bool dryRun, IReadOnlySet<string>? skipThreads)
    {
        var goals = new List<string>();
        var stopped = new List<string>();
        var live = 0;
        try
        {
            var loaded = await client.LoadedThreadsAsync(cancellationToken).ConfigureAwait(false);
            live = loaded.Count;
            foreach (var threadId in loaded.Where(id => skipThreads is null || !skipThreads.Contains(id)))
            {
                try
                {
                    var goal = await client.GetGoalAsync(threadId, cancellationToken).ConfigureAwait(false);
                    if (goal?.Status == UsageLimited)
                    {
                        if (!dryRun)
                        {
                            await client.SetGoalAsync(threadId, null, "active", cancellationToken).ConfigureAwait(false);
                        }

                        goals.Add(threadId);
                    }
                    else if (goal is null && await client.LastTurnHitUsageLimitAsync(threadId, cancellationToken).ConfigureAwait(false))
                    {
                        stopped.Add(threadId);
                    }
                }
                catch (JsonRpcException)
                {
                    // e.g. ephemeral threads have no goals; the other threads still count.
                }
            }

            return new SessionResumeReport(true, goals, stopped, LiveSessions: live);
        }
        catch (Exception ex) when (ex is JsonRpcException or CodexTransientException or InvalidOperationException or FormatException)
        {
            // Partial progress still counts: goals already resumed stay resumed.
            return new SessionResumeReport(true, goals, stopped, ex.Message, live);
        }
    }
}
