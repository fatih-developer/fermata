using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fermata.Core.Monitoring;
using Fermata.Platform;

namespace Fermata.Cli.Commands;

/// <summary>
/// <c>fermata hook &lt;event&gt;</c>: a Codex command hook (installed by <c>fermata codex install</c>).
/// Reads only the monitor's status.json, so it answers in milliseconds, never contacts Codex, never
/// blocks a prompt and always exits 0: a broken hook must not get in the way of the user's work.
/// </summary>
internal static class HookCommand
{
    public const string SessionStart = "session-start";
    public const string UserPromptSubmit = "user-prompt-submit";

    // Keeps backticks and quotes readable in Codex; the output is JSON, not HTML.
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static int Run(string hookEvent)
    {
        try
        {
            DrainStdin();
            var paths = AppPaths.Default();
            var snapshot = new JsonStatusSnapshotStore(paths.StatusFile).Load();
            var output = Respond(hookEvent, snapshot, DateTimeOffset.UtcNow);
            if (output is not null)
            {
                Console.WriteLine(output);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Silence: Codex continues as if the hook did not exist.
        }

        return ExitCodes.Ok;
    }

    /// <summary>The hook's stdout (JSON), or null for "nothing to say".</summary>
    internal static string? Respond(string hookEvent, StatusSnapshot? snapshot, DateTimeOffset now)
    {
        var message = hookEvent switch
        {
            SessionStart => CodexHookAdvisor.ForSessionStart(snapshot, now),
            UserPromptSubmit => CodexHookAdvisor.ForPrompt(snapshot, now),
            _ => null,
        };

        return message is null ? null : new JsonObject { ["systemMessage"] = message }.ToJsonString(Relaxed);
    }

    // Codex writes the event payload to stdin; read it so the writer never sees a broken pipe.
    private static void DrainStdin()
    {
        if (Console.IsInputRedirected)
        {
            Console.In.ReadToEnd();
        }
    }
}
