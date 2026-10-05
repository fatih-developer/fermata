using Fermata.Core.Ports;

namespace Fermata.Platform;

/// <summary>
/// Cross-process lock via an exclusively opened file. The OS releases it if the process dies,
/// so a crash can never leave a stale lock behind.
/// </summary>
public sealed class FileResetLock : IResetLock
{
    private readonly string _path;

    public FileResetLock(string path)
    {
        _path = path;
    }

    public IDisposable? TryAcquire()
    {
        try
        {
            // The file is never deleted: unlinking a lock file on Unix lets two processes end up
            // holding locks on different inodes.
            return new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
