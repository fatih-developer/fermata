using ResetMe.Core.Reset;

namespace ResetMe.Core.Ports;

public interface IResetStateStore
{
    ResetState Load();

    /// <summary>Must be atomic: a crash mid-write may never leave a half-written state.</summary>
    void Save(ResetState state);
}

/// <summary>Cross-process exclusive lock guarding the reset flow (FR-11).</summary>
public interface IResetLock
{
    /// <summary>Returns a handle that releases the lock on dispose, or null if another process holds it.</summary>
    IDisposable? TryAcquire();
}
