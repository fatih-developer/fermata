using Fermata.Platform.Processes;

namespace Fermata.Platform.Codex;

public sealed record CodexIntegrationStatus(CodexHookStatus Hooks, bool SkillInstalled, bool McpRegistered, bool LegacyParts = false)
{
    public bool Installed => Hooks.Installed || SkillInstalled || McpRegistered;

    /// <summary>Something still points at ResetMe (the old name); <c>fermata codex install</c> replaces it.</summary>
    public bool NeedsReinstall => LegacyParts || Hooks.IsLegacy;
}

/// <summary>
/// Everything that puts Fermata inside Codex: the hooks (messages in every session), a skill (how
/// the model should treat limits and credits) and the <c>fermata mcp</c> server (the model can read
/// usage). The MCP server is registered through <c>codex mcp add</c> so Codex writes its own config.
/// </summary>
public sealed class CodexIntegration
{
    public const string ServerName = "fermata";

    /// <summary>MCP server and skill name before the rename.</summary>
    public const string LegacyName = "resetme";

    private static readonly TimeSpan CodexTimeout = TimeSpan.FromSeconds(30);

    private readonly string? _codexExecutable;
    private readonly IProcessRunner _runner;

    /// <param name="codexExecutable">codex to run for <c>codex mcp add/remove</c>; null skips MCP registration.</param>
    public CodexIntegration(string codexHome, string? codexExecutable, IProcessRunner? runner = null)
    {
        CodexHome = codexHome;
        _codexExecutable = codexExecutable;
        _runner = runner ?? ProcessRunner.Instance;
        Hooks = new CodexHooksInstaller(codexHome);
    }

    public string CodexHome { get; }

    public CodexHooksInstaller Hooks { get; }

    public string SkillFile => Path.Combine(CodexHome, "skills", ServerName, "SKILL.md");

    private string LegacySkillDirectory => Path.Combine(CodexHome, "skills", LegacyName);

    public CodexIntegrationStatus GetStatus()
    {
        var config = Path.Combine(CodexHome, "config.toml");
        var lines = File.Exists(config) ? File.ReadAllLines(config).Select(line => line.Trim()).ToList() : [];
        bool Registered(string name) => lines.Any(line => line == $"[mcp_servers.{name}]" || line == $"[mcp_servers.\"{name}\"]");
        return new CodexIntegrationStatus(
            Hooks.GetStatus(),
            File.Exists(SkillFile),
            Registered(ServerName),
            Registered(LegacyName) || Directory.Exists(LegacySkillDirectory));
    }

    /// <summary>Installs hooks, skill and MCP server. Returns warnings for parts that were skipped.</summary>
    public async Task<IReadOnlyList<string>> InstallAsync(string fermataExecutable, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        Hooks.Install(fermataExecutable);
        if (Directory.Exists(LegacySkillDirectory))
        {
            Directory.Delete(LegacySkillDirectory, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(SkillFile)!);
        await File.WriteAllTextAsync(SkillFile, SkillText, cancellationToken).ConfigureAwait(false);

        if (_codexExecutable is null)
        {
            warnings.Add("codex was not found, so the Fermata MCP server was not registered. Run: codex mcp add fermata -- \"<path to fermata>\" mcp");
            return warnings;
        }

        await RunCodexAsync(["mcp", "remove", ServerName], cancellationToken).ConfigureAwait(false); // re-install: replace
        if (GetStatus().LegacyParts)
        {
            await RunCodexAsync(["mcp", "remove", LegacyName], cancellationToken).ConfigureAwait(false);
        }

        var add = await RunCodexAsync(["mcp", "add", ServerName, "--", fermataExecutable, "mcp"], cancellationToken).ConfigureAwait(false);
        if (!add.Succeeded)
        {
            warnings.Add($"`codex mcp add` failed: {(add.StandardError + add.StandardOutput).Trim()}");
        }

        return warnings;
    }

    /// <summary>Removes whatever of Fermata is installed. Returns true when something was removed.</summary>
    public async Task<bool> UninstallAsync(CancellationToken cancellationToken)
    {
        var status = GetStatus();
        var removed = Hooks.Uninstall();

        if (status.SkillInstalled)
        {
            Directory.Delete(Path.GetDirectoryName(SkillFile)!, recursive: true);
            removed = true;
        }

        if (status.McpRegistered && _codexExecutable is not null)
        {
            removed |= (await RunCodexAsync(["mcp", "remove", ServerName], cancellationToken).ConfigureAwait(false)).Succeeded;
        }

        if (Directory.Exists(LegacySkillDirectory))
        {
            Directory.Delete(LegacySkillDirectory, recursive: true);
            removed = true;
        }

        if (status.LegacyParts && _codexExecutable is not null)
        {
            removed |= (await RunCodexAsync(["mcp", "remove", LegacyName], cancellationToken).ConfigureAwait(false)).Succeeded;
        }

        return removed;
    }

    private async Task<ProcessResult> RunCodexAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await _runner.RunAsync(
                _codexExecutable!,
                arguments,
                CodexTimeout,
                cancellationToken,
                new Dictionary<string, string> { ["CODEX_HOME"] = CodexHome }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(-1, "", ex.Message);
        }
    }

    internal const string SkillText = """
        ---
        name: fermata
        description: Codex usage limits, reset credits and long supervised jobs via Fermata. Use when the user asks how much Codex usage is left, when a 5-hour or weekly limit is close or reached, before starting long unattended work, or when they ask about a Fermata job.
        ---

        # Fermata

        Fermata watches this account's Codex limits, can redeem a reset credit when a limit is reached, and supervises long jobs across limits (checkpoint, wait for the reset, resume).

        - Call the `codex_usage` tool (Fermata MCP server) for 5-hour and weekly usage, reset times, reset credits and whether auto-reset is on.
        - Call the `fermata_jobs` tool to see supervised jobs: status, why one waits or is blocked, and when it resumes.
        - Never redeem a reset credit yourself and never run `fermata reset`. Credits are the user's decision: point them to "Reset now…" in the Fermata tray icon, or let them run `fermata reset` themselves.
        - Before long unattended work, check `codex_usage`. If a window is above about 80%, say so, and suggest running the task as a goal (`/goal ...`) or as a Fermata job: `fermata run --provider codex --objective "…"` (optionally `--at 07:30` or `--in 2h`). Fermata stops it before the limit, records a checkpoint and resumes it after the reset.
        - The user manages jobs with `fermata jobs`, `fermata job <id>`, `fermata pause <id>`, `fermata resume <id> [--now|--force]` and `fermata cancel <id>`. Do not run these yourself unless the user asks.
        - If usage is high, credits are available and auto-reset is off, mention that the user can turn on Auto-reset in the Fermata tray.
        """;
}
