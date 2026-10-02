using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace ResetMe.Platform;

/// <summary>
/// One tray app per user: the first instance holds a lock file and listens on a named pipe;
/// a second launch asks it to show its window and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ShowCommand = "show";

    private readonly FileStream _lock;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(FileStream lockHandle, string pipeName)
    {
        _lock = lockHandle;
        _pipeName = pipeName;
    }

    /// <summary>Returns the instance guard, or null when another instance already runs.</summary>
    public static SingleInstance? TryAcquire(string lockFile, string name)
    {
        try
        {
            var handle = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(handle, PipeName(name));
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

    /// <summary>Asks the running instance to show itself. Returns false if it did not answer.</summary>
    public static bool SignalExisting(string name, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(name), PipeDirection.Out);
            client.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(ShowCommand);
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Invokes <paramref name="onShow"/> (on a background thread) for every activation request.</summary>
    public void Listen(Action onShow)
    {
        ArgumentNullException.ThrowIfNull(onShow);
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    var buffer = new byte[16];
                    var read = await server.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                    if (Encoding.UTF8.GetString(buffer, 0, read) == ShowCommand)
                    {
                        onShow();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _lock.Dispose();
    }

    /// <summary>Per-user, short (Unix socket paths are length-limited) pipe name.</summary>
    private static string PipeName(string name)
    {
        var user = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)).AsSpan(0, 4));
        return $"resetme-{name}-{user}";
    }
}
