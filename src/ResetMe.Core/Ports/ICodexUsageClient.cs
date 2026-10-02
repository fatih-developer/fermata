using ResetMe.Core.Domain;

namespace ResetMe.Core.Ports;

/// <summary>
/// The only boundary between Core and Codex. Protocol changes stay behind implementations.
/// </summary>
public interface ICodexUsageClient
{
    Task<CodexUsage> GetUsageAsync(bool includeCreditDetails, CancellationToken cancellationToken);

    Task<AccountStatus> GetAccountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Redeems one reset credit. Reusing <paramref name="idempotencyKey"/> for a retry of the
    /// same logical attempt must never consume a second credit.
    /// </summary>
    Task<ResetOutcome> ConsumeResetAsync(
        string idempotencyKey,
        string? creditId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when the outcome of a call is unknown (timeout, dropped connection).
/// For consume this means the credit may or may not have been redeemed.
/// </summary>
public sealed class CodexTransientException : Exception
{
    public CodexTransientException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
