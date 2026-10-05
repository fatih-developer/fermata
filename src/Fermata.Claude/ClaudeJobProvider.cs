using Fermata.Core.Jobs;

namespace Fermata.Claude;

public sealed class ClaudeJobProviderOptions
{
    /// <summary>Permission mode for sessions Fermata starts.</summary>
    public string PermissionMode { get; init; } = "default";

    public QuotaPolicyOptions Policy { get; init; } = new();

    /// <summary>How long after the reset Fermata trusts Claude's own auto-resume before taking over.</summary>
    public TimeSpan NativeGrace { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Claude Code jobs run as background sessions (<c>claude --bg</c>), or are interactive sessions
/// the user adopted. The session state comes from <c>claude agents --json</c> (is a process alive,
/// what did a background session end with) and from Fermata's hooks (limit hit, auto-resume fired
/// or gave up, permission and input prompts). While Claude's own auto-resume can still fire,
/// Fermata waits; it resumes with <c>claude --bg --resume</c> only when Claude will not.
/// </summary>
public sealed class ClaudeJobProvider : IJobProvider
{
    private static readonly TimeSpan AgentsCacheFor = TimeSpan.FromSeconds(10);

    private readonly ClaudeCli _cli;
    private readonly ClaudeStateStore _state;
    private readonly Func<ClaudeJobProviderOptions> _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _agentsGate = new(1, 1);
    private (DateTimeOffset At, IReadOnlyList<ClaudeAgent> Agents)? _agents;

    public ClaudeJobProvider(ClaudeCli cli, ClaudeStateStore state, Func<ClaudeJobProviderOptions> options, TimeProvider time)
    {
        _cli = cli;
        _state = state;
        _options = options;
        _time = time;
    }

    public JobProviderKind Kind => JobProviderKind.Claude;

    public ResetCreditSupport ResetCredits => ResetCreditSupport.ManualWeb;

    public bool SupportsHandoff => true;

    public QuotaSnapshot? GetQuota() => _state.Quota(_time.GetUtcNow());

    public async Task<SessionRef> StartAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var permission = job.Approvals ?? _options().PermissionMode;
        var shortId = await _cli.StartBackgroundAsync(job.Cwd, JobPrompts.Start(job), permission, $"fermata: {job.DisplayName}", cancellationToken).ConfigureAwait(false);
        Invalidate();

        // The full session id is only in `claude agents`; the background service may need a moment.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var agent = (await AgentsAsync(cancellationToken, fresh: true).ConfigureAwait(false)).FirstOrDefault(a => a.Id == shortId);
            if (agent is not null)
            {
                return new SessionRef(agent.SessionId, shortId);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(false);
        }

        return new SessionRef(shortId, shortId);
    }

    public async Task<AdoptedSession> AdoptAsync(string? sessionId, string? objective, string cwd, CancellationToken cancellationToken)
    {
        var agents = await AgentsAsync(cancellationToken, fresh: true).ConfigureAwait(false);
        ClaudeAgent? agent;
        if (sessionId is not null)
        {
            agent = agents.FirstOrDefault(a => a.SessionId == sessionId || a.Id == sessionId);
            if (agent is null && _state.LoadSession(sessionId) is { } record)
            {
                return new AdoptedSession(new SessionRef(sessionId), record.Cwd ?? cwd, Objective(objective, null));
            }
        }
        else
        {
            // Latest live session, preferring the current folder.
            var live = agents.Where(a => a.Alive).OrderByDescending(a => a.StartedAt).ToList();
            agent = live.FirstOrDefault(a => SamePath(a.Cwd, cwd)) ?? live.FirstOrDefault();
        }

        if (agent is null)
        {
            throw new JobProviderException(
                sessionId is null ? "No running Claude Code session was found." : $"Claude Code session {sessionId} was not found.",
                permanent: true);
        }

        return new AdoptedSession(new SessionRef(agent.SessionId, agent.IsBackground ? agent.Id : null), agent.Cwd ?? cwd, Objective(objective, null));
    }

    public async Task<SessionObservation> GetSessionStateAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Session is not { } session)
        {
            return SessionObservation.None;
        }

        var agent = Find(await AgentsAsync(cancellationToken).ConfigureAwait(false), session);
        var record = _state.LoadSession(agent?.SessionId ?? session.Id);
        var now = _time.GetUtcNow();
        var resumeAt = QuotaPolicy.Assess(_state.Quota(now), _options().Policy, now).ResumeAt;
        return Map(agent, record, now, resumeAt, _options().NativeGrace);
    }

    /// <summary>Process + hook records → normalized state. Pure, for tests.</summary>
    internal static SessionObservation Map(ClaudeAgent? agent, ClaudeSessionRecord? record, DateTimeOffset now, DateTimeOffset? quotaResumeAt, TimeSpan nativeGrace)
    {
        var alive = agent?.Alive == true;
        var hint = agent?.IsBackground == true ? $"open it with `claude attach {agent.Id}`" : "answer in its terminal";

        if (agent?.State == "done" || (record?.CompletedAt is { } completed && !(record.LastStartAt > completed)))
        {
            return new SessionObservation(SessionState.Completed);
        }

        if (record?.LimitOpen == true)
        {
            if (record.AutoResumeGaveUpAt > record.LimitHitAt)
            {
                return new SessionObservation(SessionState.LimitStopped, "Claude will not continue on its own");
            }

            if (!alive)
            {
                return new SessionObservation(SessionState.LimitStopped, "the session stopped at the limit and its process is gone");
            }

            // Claude's auto-resume should fire at the reset. If it has not long after, take over.
            return quotaResumeAt is { } at && now > at + nativeGrace
                ? new SessionObservation(SessionState.LimitStopped, "Claude's auto-resume did not fire")
                : new SessionObservation(SessionState.NativeWaiting, "Claude continues on its own after the reset");
        }

        if (!alive)
        {
            return record?.EndedAt is not null && !(record.LastStartAt > record.EndedAt)
                ? new SessionObservation(SessionState.Gone, $"the session ended ({record.EndReason ?? "unknown reason"})")
                : new SessionObservation(SessionState.Interrupted, "the Claude process is gone (reboot or crash)");
        }

        var pending = record?.LastNotificationAt is { } asked
            && !(record.LastStopAt > asked)
            && !(record.LastStartAt > asked)
            && !(record.AutoResumeFiredAt > asked);
        if (pending)
        {
            switch (record!.LastNotification)
            {
                case "permission_prompt" or "worker_permission_prompt":
                    return new SessionObservation(SessionState.WaitingApproval, hint);
                case "agent_needs_input" or "elicitation_dialog" or "elicitation_url_dialog" or "idle_prompt":
                    return new SessionObservation(SessionState.WaitingInput, hint);
            }
        }

        if (agent!.State == "failed")
        {
            return new SessionObservation(SessionState.WaitingInput, $"the background session failed; see `claude logs {agent.Id}`");
        }

        return agent.Status == "busy" ? new SessionObservation(SessionState.Running) : new SessionObservation(SessionState.Idle);
    }

    /// <summary>A user pause stops a background session (its conversation is kept). Quota stops wait for the turn.</summary>
    public async Task<bool> PauseAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.StopReason != StopReason.User || job.Session is not { } session)
        {
            return false;
        }

        var agent = Find(await AgentsAsync(cancellationToken, fresh: true).ConfigureAwait(false), session);
        if (agent is null || !agent.Alive)
        {
            return true;
        }

        if (!agent.IsBackground)
        {
            return false; // an interactive session stops when the user ends it
        }

        await _cli.StopAsync(agent.Id!, cancellationToken).ConfigureAwait(false);
        Invalidate();
        return true;
    }

    public async Task<SessionRef?> ResumeAsync(Job job, string prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var session = job.Session ?? throw new JobProviderException("The job has no Claude session yet.", permanent: true);
        var agent = Find(await AgentsAsync(cancellationToken, fresh: true).ConfigureAwait(false), session);
        if (agent is { Alive: true, IsBackground: false })
        {
            throw new JobProviderException("The session is still open in a terminal: continue it there. Fermata resumes it in the background once that terminal is closed.");
        }

        if (agent is { Alive: true, IsBackground: true })
        {
            await _cli.StopAsync(agent.Id!, cancellationToken).ConfigureAwait(false);
        }

        var sessionId = agent?.SessionId ?? session.Id;
        var shortId = await _cli.ResumeBackgroundAsync(job.Cwd, sessionId, prompt, cancellationToken).ConfigureAwait(false);
        Invalidate();

        // The resumed session starts a new turn: the limit episode is over (its SessionStart hook says the same).
        _state.UpdateSession(sessionId, _time.GetUtcNow(), s => s with { LastStartAt = _time.GetUtcNow() });
        return sessionId == session.Id && shortId == session.ShortId ? null : new SessionRef(sessionId, shortId);
    }

    /// <summary>The Stop hook asks for the note at the next turn end (once per episode).</summary>
    public Task<bool> RequestHandoffAsync(Job job, CancellationToken cancellationToken) => Task.FromResult(true);

    public async Task CancelAsync(Job job, CancellationToken cancellationToken)
    {
        if (job?.Session is not { } session)
        {
            return;
        }

        try
        {
            var agent = Find(await AgentsAsync(cancellationToken, fresh: true).ConfigureAwait(false), session);
            if (agent is { Alive: true, IsBackground: true })
            {
                await _cli.StopAsync(agent.Id!, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JobProviderException)
        {
            // Cancelling must work even when claude is unavailable.
        }
    }

    private static ClaudeAgent? Find(IReadOnlyList<ClaudeAgent> agents, SessionRef session) =>
        agents.Where(a => a.SessionId == session.Id || (session.ShortId is not null && a.Id == session.ShortId))
            .OrderByDescending(a => a.Alive)
            .ThenByDescending(a => a.StartedAt)
            .FirstOrDefault();

    private async Task<IReadOnlyList<ClaudeAgent>> AgentsAsync(CancellationToken cancellationToken, bool fresh = false)
    {
        await _agentsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (!fresh && _agents is { } cached && now - cached.At < AgentsCacheFor)
            {
                return cached.Agents;
            }

            var agents = await _cli.AgentsAsync(cancellationToken).ConfigureAwait(false);
            _agents = (now, agents);
            return agents;
        }
        finally
        {
            _agentsGate.Release();
        }
    }

    private void Invalidate() => _agents = null;

    private static string Objective(string? objective, string? fallback) =>
        !string.IsNullOrWhiteSpace(objective) ? objective : fallback ?? "Continue the work in this session.";

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
