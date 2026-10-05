using Fermata.Platform;
using Fermata.Platform.Diagnostics;

namespace Fermata.Cli.Commands;

/// <summary>`fermata diagnostics [--out file.zip] [--no-doctor]`.</summary>
internal static class DiagnosticsCommand
{
    public static async Task<int> RunAsync(string? output, bool skipDoctor, CancellationToken cancellationToken)
    {
        var paths = AppPaths.Default();
        paths.EnsureRoot();
        var now = TimeProvider.System.GetUtcNow();
        var file = Path.GetFullPath(output ?? DiagnosticsBundle.DefaultFileName(now));

        var extras = new Dictionary<string, string>();
        if (!skipDoctor)
        {
            Console.WriteLine("Running doctor…");
            extras["doctor.txt"] = await CaptureAsync(() => DoctorCommand.RunAsync(sendTestNotification: false, cancellationToken)).ConfigureAwait(false);
        }

        // Flush pending log lines so the bundle includes this run.
        AppLogging.Shutdown();

        DiagnosticsBundle.Create(paths, file, extras, TimeProvider.System);
        Console.WriteLine($"Diagnostics written to {file}");
        Console.WriteLine("Contains: system info, config, state, the last 7 days of logs and doctor output (sanitized).");
        Console.WriteLine("Never contains: Codex credentials, prompts, source code or e-mail addresses.");
        return ExitCodes.Ok;
    }

    private static async Task<string> CaptureAsync(Func<Task<int>> action)
    {
        var original = Console.Out;
        using var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            var code = await action().ConfigureAwait(false);
            buffer.WriteLine();
            buffer.WriteLine($"exit code: {code}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            buffer.WriteLine($"doctor failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }
}
