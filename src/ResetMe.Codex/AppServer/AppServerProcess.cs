using System.Diagnostics;
using System.Text;

namespace ResetMe.Codex.AppServer;

/// <summary>A <c>codex app-server</c> child process on the stdio transport.</summary>
public sealed class AppServerProcess : IDisposable
{
    private const int StderrLinesKept = 40;

    private readonly Process _process;
    private readonly Queue<string> _stderr = new();

    private AppServerProcess(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (_stderr)
            {
                _stderr.Enqueue(e.Data);
                if (_stderr.Count > StderrLinesKept)
                {
                    _stderr.Dequeue();
                }
            }
        };
        _process.BeginErrorReadLine();
    }

    public TextReader Output => _process.StandardOutput;

    public TextWriter Input => _process.StandardInput;

    public bool HasExited => _process.HasExited;

    /// <summary>Recent stderr lines, for diagnostics only (may contain paths, never tokens).</summary>
    public IReadOnlyList<string> RecentStderr
    {
        get
        {
            lock (_stderr)
            {
                return [.. _stderr];
            }
        }
    }

    public static AppServerProcess Start(string executable)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var info = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (CodexLocator.NeedsShell(executable))
        {
            // cmd.exe has its own quoting rules: ArgumentList would escape quotes as \" which cmd
            // does not understand, so the command line is passed verbatim. /s strips the outer quotes.
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = $"/d /s /c \"\"{executable}\" app-server\"";
        }
        else
        {
            info.FileName = executable;
            info.ArgumentList.Add("app-server");
        }

        var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start '{executable}'.");
        return new AppServerProcess(process);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(TimeSpan.FromSeconds(2)))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();
    }
}
