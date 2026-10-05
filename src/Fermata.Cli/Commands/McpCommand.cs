using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fermata.Core.Jobs;
using Fermata.Core.Monitoring;
using Fermata.Platform;
using Fermata.Platform.Jobs;

namespace Fermata.Cli.Commands;

/// <summary>
/// <c>fermata mcp</c>: a minimal MCP server on stdio (newline-delimited JSON-RPC) with two read-only
/// tools, <c>codex_usage</c> and <c>fermata_jobs</c>. It answers from status.json and the job
/// folder, so it is instant, never redeems credits and never changes a job.
/// </summary>
internal static class McpCommand
{
    public const string ToolName = "codex_usage";
    public const string JobsToolName = "fermata_jobs";
    private const string DefaultProtocolVersion = "2025-06-18";

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var paths = AppPaths.Default();
        var store = new JsonStatusSnapshotStore(paths.StatusFile);
        var jobs = new JsonJobStore(paths.JobsDirectory);
        var stdout = Console.Out;
        while (await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (Handle(line, () => store.Load(), DateTimeOffset.UtcNow, jobs.List) is { } reply)
            {
                await stdout.WriteLineAsync(reply).ConfigureAwait(false);
                await stdout.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return ExitCodes.Ok;
    }

    /// <summary>One request line → one response line (null for notifications and garbage).</summary>
    internal static string? Handle(string line, Func<StatusSnapshot?> snapshot, DateTimeOffset now, Func<IReadOnlyList<Job>>? jobs = null)
    {
        JsonObject? request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        var id = request?["id"];
        var method = request?["method"]?.GetValue<string>();
        if (request is null || id is null || method is null)
        {
            return null; // notifications (initialized, cancelled…) need no answer
        }

        var tool = request["params"]?["name"]?.GetValue<string>();
        JsonNode? result = method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = request["params"]?["protocolVersion"]?.GetValue<string>() ?? DefaultProtocolVersion,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "fermata", ["version"] = typeof(McpCommand).Assembly.GetName().Version?.ToString(3) ?? "0" },
            },
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject
            {
                ["tools"] = new JsonArray(
                    Tool(ToolName, "Current Codex usage from Fermata: 5-hour and weekly percent used, reset times, reset credits, auto-reset mode, and whether a limit is reached. Read-only; never uses a credit."),
                    Tool(JobsToolName, "Fermata's supervised jobs (Codex and Claude Code): status, why a job waits or is blocked, when it resumes next. Read-only; the user changes jobs with `fermata pause|resume|cancel <id>`.")),
            },
            "tools/call" when tool == ToolName => Text(CodexHookAdvisor.Describe(snapshot(), now)),
            "tools/call" when tool == JobsToolName => Text(DescribeJobs(jobs?.Invoke() ?? [], now)),
            _ => null,
        };

        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        if (result is null)
        {
            response["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Unknown method or tool: {method}" };
        }
        else
        {
            response["result"] = result;
        }

        return response.ToJsonString(Relaxed);
    }

    internal static string DescribeJobs(IReadOnlyList<Job> jobs, DateTimeOffset now)
    {
        var shown = jobs.Where(j => !j.IsFinished || j.FinishedAt > now.AddDays(-1)).ToList();
        if (shown.Count == 0)
        {
            return "No Fermata jobs. The user can start one with `fermata run --provider codex|claude --objective \"…\"`.";
        }

        return string.Join("\n", shown.Select(j =>
        {
            var when = j.ResumeAt is { } at && j.Status is JobStatus.WaitingQuota or JobStatus.Scheduled or JobStatus.Paused
                ? $", next step around {at.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "";
            var reason = j.BlockReason is { } r ? $" ({r})" : "";
            return $"- {j.Id} [{j.Provider.ToString().ToLowerInvariant()}] {JobsCommand.StatusText(j.Status)}{reason}{when}. Objective: {j.Objective.ReplaceLineEndings(" ")}";
        }));
    }

    private static JsonObject Tool(string name, string description) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
    };

    private static JsonObject Text(string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = false,
    };
}
