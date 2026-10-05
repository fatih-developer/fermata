using System.Text.Json;
using Fermata.Cli.Output;
using Fermata.Codex.AppServer;
using Fermata.Core.Monitoring;
using Fermata.Platform;
using Fermata.Platform.Codex;

namespace Fermata.Cli.Commands;

/// <summary><c>fermata codex status|install|uninstall|continue</c>: Fermata inside Codex.</summary>
internal static class CodexCommand
{
    public static async Task<int> RunAsync(string action, CancellationToken cancellationToken)
    {
        var options = new TomlConfigStore(AppPaths.Default().ConfigFile).Load().Options;
        var integration = new CodexIntegration(CodexHooksInstaller.DefaultCodexHome(), CodexLocator.Resolve(options.CodexExecutable));
        try
        {
            return action switch
            {
                "install" => await InstallAsync(integration, cancellationToken).ConfigureAwait(false),
                "uninstall" => await UninstallAsync(integration, cancellationToken).ConfigureAwait(false),
                "continue" => await ContinueAsync(integration, cancellationToken).ConfigureAwait(false),
                _ => await StatusAsync(integration, options.ContinueAfterReset, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not update Codex settings in {integration.CodexHome}: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    private static async Task<int> InstallAsync(CodexIntegration integration, CancellationToken cancellationToken)
    {
        var executable = SelfExecutable();
        if (executable is null)
        {
            Console.Error.WriteLine("Cannot determine the fermata executable path. Run the installed `fermata`, not `dotnet run`.");
            return ExitCodes.Error;
        }

        var warnings = await integration.InstallAsync(executable, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Added Fermata to Codex ({integration.CodexHome}):");
        Console.WriteLine($"  hooks       SessionStart, UserPromptSubmit → {executable} hook …");
        Console.WriteLine($"  skill       {integration.SkillFile}");
        Console.WriteLine(warnings.Count == 0 ? $"  MCP server  {CodexIntegration.ServerName} → {executable} mcp" : "  MCP server  not registered");
        foreach (var warning in warnings)
        {
            Console.WriteLine($"  warning: {warning}");
        }

        Console.WriteLine();
        Console.WriteLine("Next steps:");
        Console.WriteLine("  1. Start Codex. It lists the new hooks for review: choose \"Trust all and continue\" (or use /hooks).");
        Console.WriteLine("  2. In the Codex TUI run /statusline and add five-hour-limit and weekly-limit.");
        Console.WriteLine("  3. Keep the Fermata app (or `fermata daemon`) running: hooks and the MCP server read its status.");
        Console.WriteLine("  4. Run long unattended work as a goal (/goal …): after a reset Fermata resumes it.");
        return ExitCodes.Ok;
    }

    private static async Task<int> UninstallAsync(CodexIntegration integration, CancellationToken cancellationToken)
    {
        Console.WriteLine(await integration.UninstallAsync(cancellationToken).ConfigureAwait(false)
            ? $"Removed Fermata from Codex ({integration.CodexHome})."
            : "Fermata was not installed in Codex.");
        return ExitCodes.Ok;
    }

    /// <summary>Resumes usage-limited goals now (e.g. after the limit lifted on its own).</summary>
    private static async Task<int> ContinueAsync(CodexIntegration integration, CancellationToken cancellationToken)
    {
        var report = await new CodexSessionResumer().ResumeAsync(integration.CodexHome, cancellationToken, skipThreads: SessionContinuation.JobThreads()).ConfigureAwait(false);
        Console.WriteLine(!report.DaemonReachable
            ? $"No shared Codex daemon is reachable, so there are no live sessions to continue. ({report.Error})"
            : report.Describe() ?? "No Codex session is waiting on the usage limit.");
        return ExitCodes.Ok;
    }

    private static async Task<int> StatusAsync(CodexIntegration integration, bool continueAfterReset, CancellationToken cancellationToken)
    {
        var status = integration.GetStatus();
        var hooks = status.Hooks;
        Console.WriteLine($"Codex home         {integration.CodexHome}");
        Console.WriteLine(Format.Rule);
        Console.WriteLine(hooks.Installed
            ? $"Hooks              installed ({string.Join(", ", hooks.InstalledEvents)})"
            : "Hooks              not installed (run `fermata codex install`)");
        if (hooks.Installed && !hooks.ExecutableExists)
        {
            Console.WriteLine($"                   warning: the hook command points to a missing file: {hooks.Command}");
        }

        if (status.NeedsReinstall)
        {
            Console.WriteLine("                   warning: parts still point to ResetMe (the old name); run `fermata codex install` to replace them");
        }

        Console.WriteLine($"Skill              {(status.SkillInstalled ? "installed" : "not installed")}");
        Console.WriteLine($"MCP server         {(status.McpRegistered ? "registered" : "not registered")}");

        var snapshot = new JsonStatusSnapshotStore(AppPaths.Default().StatusFile).Load();
        var now = DateTimeOffset.UtcNow;
        Console.WriteLine(snapshot is not null && snapshot.IsFresh(now)
            ? $"Monitor            running ({snapshot.Source}, updated {Format.Duration(now - snapshot.UpdatedAt)} ago)"
            : "Monitor            not running: start the Fermata app or `fermata daemon` (Codex sessions will say protection is off)");

        var sessions = await new CodexSessionResumer().ResumeAsync(integration.CodexHome, cancellationToken, dryRun: true).ConfigureAwait(false);
        Console.WriteLine(sessions.DaemonReachable
            ? $"Codex daemon       reachable, {sessions.LiveSessions} live session(s), {sessions.GoalsResumed.Count} goal(s) waiting on the limit"
            : "Codex daemon       not reachable: sessions outside the shared daemon cannot be continued automatically");
        Console.WriteLine($"Auto-continue      {(continueAfterReset ? "on" : "off")} ([codex] continue_after_reset)");
        Console.WriteLine();
        Console.WriteLine(CodexHookAdvisor.Describe(snapshot, now));
        return ExitCodes.Ok;
    }

    /// <summary>The fermata executable to put in the hook (not dotnet.exe when run via `dotnet fermata.dll`).</summary>
    internal static string? SelfExecutable()
    {
        var process = Environment.ProcessPath;
        if (process is not null && Path.GetFileNameWithoutExtension(process).Equals("fermata", StringComparison.OrdinalIgnoreCase))
        {
            return process;
        }

        var sibling = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fermata.exe" : "fermata");
        return File.Exists(sibling) ? sibling : null;
    }
}
