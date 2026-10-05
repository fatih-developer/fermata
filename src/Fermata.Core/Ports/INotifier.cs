namespace Fermata.Core.Ports;

public enum NotificationKind
{
    LimitReached,
    ResetSucceeded,
    ResetProblem,
    Info,
}

public sealed record Notification(NotificationKind Kind, string Title, string Body);

/// <summary>Platform-native desktop notification (PRD FR-07). Must never throw.</summary>
public interface INotifier
{
    /// <summary>Short name of the mechanism, for doctor output (e.g. "toast", "osascript", "notify-send").</summary>
    string Mechanism { get; }

    bool IsAvailable { get; }

    /// <summary>Returns false when the notification could not be shown; callers fall back to the terminal/log.</summary>
    Task<bool> ShowAsync(Notification notification, CancellationToken cancellationToken);
}
