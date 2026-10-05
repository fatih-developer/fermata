namespace Fermata.Core.Jobs;

/// <summary>How a provider's reset credits can be used.</summary>
public enum ResetCreditSupport
{
    None,

    /// <summary>Fermata can redeem one (Codex: the monitor's manual/confirm/automatic mode decides).</summary>
    Api,

    /// <summary>Only the user can, on the provider's web page (Claude "Limit resets"): Fermata suggests it.</summary>
    ManualWeb,
}

/// <summary>What the provider's session is doing right now, normalized.</summary>
public enum SessionState
{
    /// <summary>Could not tell (provider unreachable); nothing is decided on it.</summary>
    Unknown,
    Running,

    /// <summary>No turn runs and nothing is pending.</summary>
    Idle,
    WaitingApproval,
    WaitingInput,

    /// <summary>The provider's own mechanism will continue after the limit (Claude auto-resume armed, process alive).</summary>
    NativeWaiting,

    /// <summary>Stopped at the limit and nothing native will continue it: Fermata resumes it later.</summary>
    LimitStopped,

    /// <summary>
    /// Stopped without finishing and without a limit (daemon restart, reboot, crash): nothing will
    /// continue it unless Fermata does, as soon as quota and workspace allow.
    /// </summary>
    Interrupted,

    /// <summary>Paused on the provider side (Codex goal paused).</summary>
    Paused,
    Completed,

    /// <summary>The session or its process no longer exists.</summary>
    Gone,
}

public sealed record SessionObservation(SessionState State, string? Detail = null)
{
    public static readonly SessionObservation Unknown = new(SessionState.Unknown);
    public static readonly SessionObservation None = new(SessionState.Idle);
}

/// <summary>An existing session the user asked Fermata to supervise.</summary>
public sealed record AdoptedSession(SessionRef Session, string Cwd, string Objective);

/// <summary>Start, observe, stop and continue sessions of one agent CLI (Codex, Claude Code).</summary>
public interface IJobProvider
{
    JobProviderKind Kind { get; }

    ResetCreditSupport ResetCredits { get; }

    /// <summary>The agent can be asked to write <c>.fermata/handoff.md</c> before stopping (Claude Stop hook).</summary>
    bool SupportsHandoff { get; }

    /// <summary>Starts a new session for <paramref name="job"/> and returns its identity.</summary>
    Task<SessionRef> StartAsync(Job job, CancellationToken cancellationToken);

    /// <summary>Finds the session to adopt: an id, or the most recent one when <paramref name="sessionId"/> is null.</summary>
    Task<AdoptedSession> AdoptAsync(string? sessionId, string? objective, string cwd, CancellationToken cancellationToken);

    Task<SessionObservation> GetSessionStateAsync(Job job, CancellationToken cancellationToken);

    /// <summary>Stop after the current turn. True when the session is already stopped.</summary>
    Task<bool> PauseAsync(Job job, CancellationToken cancellationToken);

    /// <summary>
    /// Continue the session with <paramref name="prompt"/> (providers that drive turns themselves may
    /// ignore it). Returns the new session identity when it changed (a new background id), else null.
    /// </summary>
    Task<SessionRef?> ResumeAsync(Job job, string prompt, CancellationToken cancellationToken);

    /// <summary>Ask for the handoff note at the next turn end. False when not supported.</summary>
    Task<bool> RequestHandoffAsync(Job job, CancellationToken cancellationToken);

    Task CancelAsync(Job job, CancellationToken cancellationToken);

    /// <summary>Latest known quota for this provider's account; null when nothing was seen.</summary>
    QuotaSnapshot? GetQuota();
}

/// <summary>Thrown by providers for failures worth retrying later (daemon down, CLI busy).</summary>
public sealed class JobProviderException : Exception
{
    public JobProviderException(string message, Exception? innerException = null, bool permanent = false)
        : base(message, innerException)
    {
        Permanent = permanent;
    }

    /// <summary>Retrying will not help (unknown session, missing CLI): the job fails.</summary>
    public bool Permanent { get; }
}
