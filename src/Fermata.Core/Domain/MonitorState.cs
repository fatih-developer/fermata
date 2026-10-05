namespace Fermata.Core.Domain;

/// <summary>Canonical state list (PRD §17). Shared by the state machine, CLI and UI.</summary>
public enum MonitorState
{
    Starting,
    Healthy,
    NearLimit,
    LimitReached,
    ResetAvailable,
    AwaitingConfirmation,
    Resetting,
    Verifying,
    ResetSucceeded,
    ResetFailed,
    ResetUnconfirmed,
    AuthRequired,
    Unavailable,
}

/// <summary>Result of <c>account/rateLimitResetCredit/consume</c>.</summary>
public enum ResetOutcome
{
    Reset,
    NothingToReset,
    NoCredit,
    AlreadyRedeemed,
}
