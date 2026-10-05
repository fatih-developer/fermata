using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fermata.Core.Jobs;
using Fermata.Platform.Processes;

namespace Fermata.Claude;

/// <summary>Finds the <c>claude</c> executable: the configured path, PATH, then the native installer's folder.</summary>
public static class ClaudeLocator
{
    public static string? Resolve(string? configured, IProcessRunner? runner = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        runner ??= ProcessRunner.Instance;
        if (runner.FindOnPath("claude") is { } onPath)
        {
            return onPath;
        }

        if (OperatingSystem.IsWindows() && runner.FindOnPath("claude.cmd") is { } npm)
        {
            return npm;
        }

        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        return File.Exists(local) ? local : null;
    }
}

/// <summary>One entry of <c>claude agents --json --all</c>.</summary>
/// <param name="Id">Background id (first 8 characters of the session id); null for interactive sessions.</param>
/// <param name="Kind">"background" or "interactive".</param>
/// <param name="Status">Process activity, e.g. "busy" or "idle"; null when the process is gone.</param>
/// <param name="State">Background outcome, e.g. "running", "done", "failed".</param>
/// <param name="StartedAt">Unix milliseconds.</param>
public sealed record ClaudeAgent(string? Id, string SessionId, string? Cwd, string Kind, int? Pid, string? Status, string? State, long StartedAt = 0)
{
    public bool IsBackground => Kind == "background";

    /// <summary>A process runs for it (Claude lists finished background sessions without a pid).</summary>
    public bool Alive => Pid is not null;
}

/// <summary>
/// The Claude Code commands Fermata uses. Observed on 2.1.289: <c>--bg</c> ignores
/// <c>--session-id</c> and prints <c>backgrounded · &lt;id&gt;</c>; <c>--bg --resume &lt;session&gt;</c>
/// continues the same session only when no other flags are passed (with flags it starts a copy);
/// <c>--bg</c> refuses folders whose workspace trust was never accepted.
/// </summary>
public sealed partial class ClaudeCli
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<string?> _executable;
    private readonly IProcessRunner _runner;

    public ClaudeCli(Func<string?> executable, IProcessRunner? runner = null)
    {
        _executable = executable;
        _runner = runner ?? ProcessRunner.Instance;
    }

    public async Task<IReadOnlyList<ClaudeAgent>> AgentsAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync("", ["agents", "--json", "--all"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new JobProviderException($"`claude agents --json` failed: {Trim(result)}");
        }

        return ParseAgents(result.StandardOutput);
    }

    /// <summary>Starts a background session; returns its background id.</summary>
    public async Task<string> StartBackgroundAsync(string cwd, string prompt, string permissionMode, string name, CancellationToken cancellationToken)
    {
        var result = await RunAsync(cwd, ["--bg", "--permission-mode", permissionMode, "--name", name, prompt], cancellationToken).ConfigureAwait(false);
        return BackgroundIdOf(result);
    }

    /// <summary>Continues <paramref name="sessionId"/> in the background. No other flags: they would start a copy.</summary>
    public async Task<string> ResumeBackgroundAsync(string cwd, string sessionId, string prompt, CancellationToken cancellationToken)
    {
        var result = await RunAsync(cwd, ["--bg", "--resume", sessionId, prompt], cancellationToken).ConfigureAwait(false);
        return BackgroundIdOf(result);
    }

    public async Task StopAsync(string backgroundId, CancellationToken cancellationToken)
    {
        var result = await RunAsync("", ["stop", backgroundId], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded && !Trim(result).Contains("not running", StringComparison.OrdinalIgnoreCase))
        {
            throw new JobProviderException($"`claude stop {backgroundId}` failed: {Trim(result)}");
        }
    }


    /// <summary>Registers <c>fermata mcp</c> for all projects (<c>claude mcp add --scope user</c>); replaces an older entry.</summary>
    public async Task<bool> AddMcpServerAsync(string fermataExecutable, CancellationToken cancellationToken)
    {
        await RunAsync("", ["mcp", "remove", "--scope", "user", McpServerName], cancellationToken).ConfigureAwait(false);
        var result = await RunAsync("", ["mcp", "add", "--scope", "user", McpServerName, "--", fermataExecutable, "mcp"], cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    public async Task<bool> RemoveMcpServerAsync(CancellationToken cancellationToken) =>
        (await RunAsync("", ["mcp", "remove", "--scope", "user", McpServerName], cancellationToken).ConfigureAwait(false)).Succeeded;

    public const string McpServerName = "fermata";

    internal static IReadOnlyList<ClaudeAgent> ParseAgents(string json)
    {
        JsonArray? array;
        try
        {
            array = JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException ex)
        {
            throw new JobProviderException($"Unexpected `claude agents --json` output: {ex.Message}");
        }

        return (array ?? []).OfType<JsonObject>()
            .Where(a => a["sessionId"] is JsonValue)
            .Select(a => new ClaudeAgent(
                a["id"]?.GetValue<string>(),
                a["sessionId"]!.GetValue<string>(),
                a["cwd"]?.GetValue<string>(),
                a["kind"]?.GetValue<string>() ?? "",
                a["pid"] is JsonValue pid && pid.TryGetValue<int>(out var p) ? p : null,
                a["status"]?.GetValue<string>(),
                a["state"]?.GetValue<string>(),
                a["startedAt"] is JsonValue started && started.TryGetValue<long>(out var at) ? at : 0))
            .ToList();
    }

    /// <summary>The id from <c>backgrounded · e92725ea</c>; refusals become permanent errors.</summary>
    internal static string BackgroundIdOf(ProcessResult result)
    {
        var output = result.StandardOutput + "\n" + result.StandardError;
        if (BackgroundLine().Match(output) is { Success: true } match)
        {
            return match.Groups[1].Value;
        }

        if (output.Contains("not trusted", StringComparison.OrdinalIgnoreCase))
        {
            throw new JobProviderException(
                $"Claude Code does not trust this folder yet. Run `claude` there once and accept the trust prompt, then resume the job. ({Trim(result)})",
                permanent: true);
        }

        throw new JobProviderException($"Claude did not start a background session: {Trim(result)}");
    }

    private async Task<ProcessResult> RunAsync(string cwd, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var claude = _executable()
            ?? throw new JobProviderException("claude was not found. Install Claude Code or set [jobs.claude] executable.", permanent: true);
        try
        {
            return await _runner.RunInAsync(cwd, claude, Flatten(claude, args), CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new JobProviderException($"claude {args[0]} failed: {ex.Message}", ex);
        }
    }

    /// <summary>An npm install runs claude.cmd through cmd.exe, which cuts arguments at a line break.</summary>
    private static IReadOnlyList<string> Flatten(string executable, IReadOnlyList<string> args) =>
        executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            ? args.Select(a => a.ReplaceLineEndings(" ")).ToList()
            : args;

    private static string Trim(ProcessResult result)
    {
        var text = (result.StandardError + " " + result.StandardOutput).Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    [GeneratedRegex(@"backgrounded[^0-9a-zA-Z]+([0-9a-f]{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundLine();
}
