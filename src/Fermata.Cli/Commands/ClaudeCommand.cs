using System.Text;
using System.Text.Json;
using Fermata.Claude;
using Fermata.Cli.Output;
using Fermata.Core.Jobs;
using Fermata.Platform;
using Fermata.Platform.Jobs;

namespace Fermata.Cli.Commands;

/// <summary><c>fermata claude install|uninstall|status</c>, plus the hidden <c>statusline</c> and <c>hook</c> entry points.</summary>
internal static class ClaudeCommand
{
    public static async Task<int> RunAsync(string action, CancellationToken cancellationToken)
    {
        var paths = AppPaths.Default();
        var options = new TomlConfigStore(paths.ConfigFile).Load().Options;
        var state = new ClaudeStateStore(paths);
        var installer = new ClaudeSettingsInstaller(ClaudeSettingsInstaller.DefaultClaudeHome(), state);
        try
        {
            return action switch
            {
                "install" => await InstallAsync(installer, Cli(options.Jobs.Claude.Executable), cancellationToken).ConfigureAwait(false),
                "uninstall" => await UninstallAsync(installer, Cli(options.Jobs.Claude.Executable), cancellationToken).ConfigureAwait(false),
                _ => Status(installer, state, options.Jobs.Claude.Executable, options.Jobs.Claude.LimitResetsUrl),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not update Claude Code settings in {installer.ClaudeHome}: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    private static ClaudeCli Cli(string configured) => new(() => ClaudeLocator.Resolve(configured));

    private static async Task<int> InstallAsync(ClaudeSettingsInstaller installer, ClaudeCli cli, CancellationToken cancellationToken)
    {
        var executable = CodexCommand.SelfExecutable();
        if (executable is null)
        {
            Console.Error.WriteLine("Cannot determine the fermata executable path. Run the installed `fermata`, not `dotnet run`.");
            return ExitCodes.Error;
        }

        installer.Install(executable);
        var status = installer.GetStatus();
        Console.WriteLine($"Added Fermata to Claude Code ({installer.SettingsFile}, backup: settings.json.fermata.bak):");
        Console.WriteLine($"  status line  {executable} claude statusline{(status.PreviousStatusLine is { } previous ? $" → then runs your previous one: {previous}" : "")}");
        Console.WriteLine($"  hooks        {string.Join(", ", status.HookEvents)} → {executable} claude hook …");
        Console.WriteLine($"  skill        {installer.SkillFile}");
        bool mcp;
        try
        {
            mcp = await cli.AddMcpServerAsync(executable, cancellationToken).ConfigureAwait(false);
        }
        catch (JobProviderException ex)
        {
            mcp = false;
            Console.WriteLine($"  warning: {ex.Message}");
        }

        Console.WriteLine(mcp ? $"  MCP server   {ClaudeCli.McpServerName} → {executable} mcp (user scope)" : $"  MCP server   not registered. Run: claude mcp add --scope user fermata -- \"{executable}\" mcp");
        Console.WriteLine();
        Console.WriteLine("Next steps:");
        Console.WriteLine("  1. Restart running Claude Code sessions so they load the new hooks.");
        Console.WriteLine("  2. Keep the Fermata app (or `fermata daemon`) running: it resumes jobs after limits.");
        Console.WriteLine("  3. Start supervised work with `fermata run --provider claude --objective \"…\"`, or adopt a session with `fermata adopt --provider claude --latest`.");
        return ExitCodes.Ok;
    }

    private static async Task<int> UninstallAsync(ClaudeSettingsInstaller installer, ClaudeCli cli, CancellationToken cancellationToken)
    {
        var mcp = false;
        try
        {
            mcp = await cli.RemoveMcpServerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (JobProviderException)
        {
            // claude missing: the settings part still goes.
        }

        Console.WriteLine(installer.Uninstall() || mcp
            ? $"Removed Fermata from Claude Code ({installer.SettingsFile}); your previous status line is back."
            : "Fermata was not installed in Claude Code.");
        return ExitCodes.Ok;
    }

    private static int Status(ClaudeSettingsInstaller installer, ClaudeStateStore state, string configuredExecutable, string limitResetsUrl)
    {
        var status = installer.GetStatus();
        var claude = ClaudeLocator.Resolve(configuredExecutable);
        Console.WriteLine($"Claude settings    {installer.SettingsFile}");
        Console.WriteLine(Format.Rule);
        Console.WriteLine($"claude             {claude ?? "not found (install Claude Code or set [jobs.claude] executable)"}");
        Console.WriteLine(status.StatusLineWrapped
            ? $"Status line        wrapped{(status.PreviousStatusLine is { } previous ? $" (then runs: {previous})" : "")}"
            : "Status line        not wrapped (run `fermata claude install`): Fermata cannot see Claude's quota");
        Console.WriteLine($"Skill              {(status.SkillInstalled ? "installed" : "not installed")}");
        Console.WriteLine(status.HookEvents.Count > 0
            ? $"Hooks              installed ({string.Join(", ", status.HookEvents)})"
            : "Hooks              not installed (run `fermata claude install`)");
        if (status.Installed && !status.ExecutableExists)
        {
            Console.WriteLine($"                   warning: the hooks point to a missing file: {status.Command}");
        }

        var now = DateTimeOffset.UtcNow;
        var quota = state.LoadQuota();
        Console.WriteLine(quota is null
            ? "Quota              not seen yet (it arrives with the next status line update of a running session)"
            : $"Quota              5h {Percent(quota.FiveHour)}, 7d {Percent(quota.SevenDay)} (seen {Format.Duration(now - quota.CapturedAt)} ago)");
        var open = state.Sessions().Where(s => s.LimitOpen).ToList();
        if (open.Count > 0)
        {
            Console.WriteLine($"Limit              {open.Count} session(s) stopped at the usage limit");
        }

        Console.WriteLine($"Reset credits      only on claude.ai: {limitResetsUrl} (\"Limit resets\")");
        return ExitCodes.Ok;
    }

    private static string Percent(ClaudeWindow? window) => window is null ? "n/a" : $"{window.UsedPercent:0}%";

    /// <summary>Status line wrapper. Always prints something and exits 0.</summary>
    public static async Task<int> StatusLineAsync(CancellationToken cancellationToken)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var input = Console.IsInputRedirected ? await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false) : "";
        string output;
        try
        {
            output = await new ClaudeStatusLine(new ClaudeStateStore(AppPaths.Default()), TimeProvider.System).RunAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            output = "Fermata";
        }

        Console.Write(output);
        return ExitCodes.Ok;
    }

    /// <summary>Hook entry point. Never fails the hook: Claude Code continues as if it did not exist.</summary>
    public static async Task<int> HookAsync(string hookEvent, CancellationToken cancellationToken)
    {
        try
        {
            var input = Console.IsInputRedirected ? await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false) : "{}";
            var paths = AppPaths.Default();
            var options = new TomlConfigStore(paths.ConfigFile).Load().Options;
            var handler = new ClaudeHookHandler(
                new ClaudeStateStore(paths),
                new JsonJobStore(paths.JobsDirectory),
                options.Jobs.QuotaPolicy,
                new CheckpointWriter(),
                options.Jobs.SavePatch,
                TimeProvider.System);
            if (await handler.HandleAsync(hookEvent, input, cancellationToken).ConfigureAwait(false) is { } output)
            {
                Console.WriteLine(output);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Tomlyn.TomlException)
        {
            // Silence: never block or fail the user's session.
        }

        return ExitCodes.Ok;
    }

    public static readonly string[] HookEvents = ["session-start", "session-end", "notification", "stop", "stop-failure"];
}
