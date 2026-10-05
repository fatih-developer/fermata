using System.CommandLine;
using System.Text;
using Microsoft.Extensions.Logging;
using Fermata.Cli;
using Fermata.Cli.Commands;
using Fermata.Codex.AppServer;
using Fermata.Codex.JsonRpc;
using Fermata.Core.Ports;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var root = new RootCommand("Fermata — supervises long Codex and Claude Code jobs across usage limits: checkpoint, wait for the reset, resume. Also watches Codex limits and redeems reset credits with your consent.");

var json = new Option<bool>("--json") { Description = "Machine-readable output." };
var status = new Command("status", "Show 5-hour/weekly usage, reset credits and what can be done.") { json };
status.SetAction((parse, ct) => Guarded("status", () => StatusCommand.RunAsync(parse.GetValue(json), ct)));

var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
var force = new Option<bool>("--force") { Description = "Reset even if the limit lifts soon, the episode was already handled, or the cooldown is active." };
var verbose = new Option<bool>("--verbose", "-v") { Description = "Print state transitions and the raw outcome." };
var reset = new Command("reset", "Redeem one reset credit when Codex is rate-limited.") { yes, force, verbose };
reset.SetAction((parse, ct) => Guarded("reset", () => ResetCommand.RunAsync(
    parse.GetValue(yes), parse.GetValue(force), parse.GetValue(verbose), ct)));

var watch = new Command("watch", "Monitor usage and offer a reset when a limit is reached (Ctrl+C to stop).");
watch.SetAction((_, ct) => Guarded("watch", () => WatchCommand.RunAsync(ct)));

var notifyTest = new Option<bool>("--notify") { Description = "Also send a test desktop notification." };
var doctor = new Command("doctor", "Check installation, Codex connectivity and reset capability.") { notifyTest };
doctor.SetAction((parse, ct) => Guarded("doctor", () => DoctorCommand.RunAsync(parse.GetValue(notifyTest), ct)));

var daemon = new Command("daemon", "Run the monitor headless (for login services, SSH and servers). Never prompts.");
daemon.SetAction((_, ct) => Guarded("daemon", () => DaemonCommand.RunAsync(ct)));

var autostartAction = new Argument<string>("action") { Description = "status | enable | disable", DefaultValueFactory = _ => "status" };
autostartAction.AcceptOnlyFromAmong("status", "enable", "disable");
var autostartTarget = new Option<string?>("--target") { Description = "desktop (tray app) or daemon (headless). Default: desktop, or daemon on headless Linux." };
autostartTarget.AcceptOnlyFromAmong("desktop", "daemon");
var autostartPath = new Option<string?>("--path") { Description = "Executable to start (default: the installed app next to fermata)." };
var autostart = new Command("autostart", "Start Fermata at login.") { autostartAction, autostartTarget, autostartPath };
autostart.SetAction((parse, ct) => Guarded("autostart", () => AutostartCommand.RunAsync(
    parse.GetValue(autostartAction)!, parse.GetValue(autostartTarget), parse.GetValue(autostartPath), ct)));

var init = new Option<bool>("--init") { Description = "Create config.toml with defaults if missing." };
var config = new Command("config", "Show the effective configuration.") { init };
config.SetAction(parse => ConfigCommand.Run(parse.GetValue(init)));

var tail = new Option<int>("--tail", "-n") { Description = "Number of entries to show.", DefaultValueFactory = _ => 50 };
var logsJson = new Option<bool>("--json") { Description = "Print raw JSON lines." };
var logsPath = new Option<bool>("--path") { Description = "Print the log directory and exit." };

root.Subcommands.Add(status);
root.Subcommands.Add(watch);
root.Subcommands.Add(reset);
root.Subcommands.Add(doctor);
root.Subcommands.Add(daemon);
root.Subcommands.Add(autostart);
root.Subcommands.Add(config);

var logs = new Command("logs", "Show recent log entries.") { tail, logsJson, logsPath };
logs.SetAction(parse => LogsCommand.Run(parse.GetValue(tail), parse.GetValue(logsJson), parse.GetValue(logsPath)));
root.Subcommands.Add(logs);

var diagnosticsOut = new Option<string?>("--out", "-o") { Description = "Zip file to write (default: ./fermata-diagnostics-<time>.zip)." };
var noDoctor = new Option<bool>("--no-doctor") { Description = "Skip running doctor (no Codex connection)." };
var diagnostics = new Command("diagnostics", "Export a sanitized diagnostics zip (config, state, logs, doctor).") { diagnosticsOut, noDoctor };
diagnostics.SetAction((parse, ct) => Guarded("diagnostics", () => DiagnosticsCommand.RunAsync(parse.GetValue(diagnosticsOut), parse.GetValue(noDoctor), ct)));
root.Subcommands.Add(diagnostics);

var updateCheck = new Option<bool>("--check") { Description = "Only report whether a newer release exists." };
var updateYes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
var update = new Command("update", "Update Fermata from GitHub Releases (verified with SHA256SUMS).") { updateCheck, updateYes };
update.SetAction((parse, ct) => Guarded("update", () => UpdateCommand.RunAsync(parse.GetValue(updateCheck), parse.GetValue(updateYes), ct)));
root.Subcommands.Add(update);

var codexAction = new Argument<string>("action") { Description = "status | install | uninstall | continue", DefaultValueFactory = _ => "status" };
codexAction.AcceptOnlyFromAmong("status", "install", "uninstall", "continue");
var codex = new Command("codex", "Show Fermata inside Codex: install or remove the Codex hooks.") { codexAction };
codex.SetAction((parse, ct) => CodexCommand.RunAsync(parse.GetValue(codexAction)!, ct));
root.Subcommands.Add(codex);

var mcp = new Command("mcp", "MCP server for Codex (registered by `fermata codex install`): read-only codex_usage tool.");
mcp.Hidden = true;
mcp.SetAction((_, ct) => McpCommand.RunAsync(ct));
root.Subcommands.Add(mcp);

var hookEvent = new Argument<string>("event") { Description = $"{HookCommand.SessionStart} | {HookCommand.UserPromptSubmit}" };
hookEvent.AcceptOnlyFromAmong(HookCommand.SessionStart, HookCommand.UserPromptSubmit);
var hook = new Command("hook", "Codex hook handler (installed by `fermata codex install`). Reads status.json only; never blocks.") { hookEvent };
hook.Hidden = true;
hook.SetAction(parse => HookCommand.Run(parse.GetValue(hookEvent)!));
root.Subcommands.Add(hook);

// ── Supervised jobs ────────────────────────────────────────────────────────────
var providerOption = new Option<string>("--provider", "-p") { Description = "codex or claude.", Required = true };
providerOption.AcceptOnlyFromAmong("codex", "claude");
var runObjective = new Option<string?>("--objective", "-o") { Description = "What the agent should do." };
var runGoal = new Option<string?>("--goal") { Description = "Read the objective from this file." };
var runCwd = new Option<string?>("--cwd") { Description = "Working folder (default: the current folder)." };
var runName = new Option<string?>("--name") { Description = "Short name; also the job id." };
var runAt = new Option<string?>("--at") { Description = "Start at this local time (HH:mm or \"yyyy-MM-dd HH:mm\")." };
var runIn = new Option<string?>("--in") { Description = "Start after this long (e.g. 45m, 2h, 1h30m)." };
var runApprovals = new Option<string?>("--approvals") { Description = "Codex: user | auto_review. Claude: permission mode (default, acceptEdits, plan…)." };
var run = new Command("run", "Start a supervised job: checkpoints before limits, waits for the reset, resumes.")
{
    providerOption, runObjective, runGoal, runCwd, runName, runAt, runIn, runApprovals,
};
run.SetAction((parse, ct) => Guarded("run", () => JobsCommand.RunAsync(
    parse.GetValue(providerOption)!, parse.GetValue(runObjective), parse.GetValue(runGoal), parse.GetValue(runCwd),
    parse.GetValue(runName), parse.GetValue(runAt), parse.GetValue(runIn), parse.GetValue(runApprovals), ct)));
root.Subcommands.Add(run);

var adoptId = new Argument<string?>("session") { Description = "Codex thread id or Claude session id.", Arity = ArgumentArity.ZeroOrOne };
var adoptLatest = new Option<bool>("--latest") { Description = "The most recent live session." };
var adoptObjective = new Option<string?>("--objective", "-o") { Description = "The task (required for a Codex thread without a goal)." };
var adoptName = new Option<string?>("--name") { Description = "Short name; also the job id." };
var adopt = new Command("adopt", "Supervise a session you already have.") { providerOption, adoptId, adoptLatest, adoptObjective, adoptName };
adopt.SetAction((parse, ct) => Guarded("adopt", () => JobsCommand.AdoptAsync(
    parse.GetValue(providerOption)!, parse.GetValue(adoptId), parse.GetValue(adoptLatest), parse.GetValue(adoptObjective), parse.GetValue(adoptName), ct)));
root.Subcommands.Add(adopt);

var jobsJson = new Option<bool>("--json") { Description = "Machine-readable output." };
var jobsAll = new Option<bool>("--all") { Description = "Include jobs that finished more than a day ago." };
var jobs = new Command("jobs", "List supervised jobs.") { jobsJson, jobsAll };
jobs.SetAction(parse => GuardedSync("jobs", () => JobsCommand.List(parse.GetValue(jobsJson), parse.GetValue(jobsAll))));
root.Subcommands.Add(jobs);

var jobId = new Argument<string>("id") { Description = "Job id (see `fermata jobs`)." };
var job = new Command("job", "Show one job with its recent events.") { jobId, jobsJson };
job.SetAction(parse => GuardedSync("job", () => JobsCommand.Show(parse.GetValue(jobId)!, parse.GetValue(jobsJson))));
root.Subcommands.Add(job);

var pauseAt = new Option<string?>("--resume-at") { Description = "Resume on its own at this local time." };
var pauseIn = new Option<string?>("--resume-in") { Description = "Resume on its own after this long." };
var pause = new Command("pause", "Pause a job after its current turn.") { jobId, pauseAt, pauseIn };
pause.SetAction((parse, ct) => Guarded("pause", () => JobsCommand.PauseAsync(parse.GetValue(jobId)!, parse.GetValue(pauseAt), parse.GetValue(pauseIn), ct)));
root.Subcommands.Add(pause);

var resumeAt = new Option<string?>("--at") { Description = "Resume at this local time." };
var resumeIn = new Option<string?>("--in") { Description = "Resume after this long." };
var resumeQuota = new Option<bool>("--when-quota-available") { Description = "Resume as soon as the quota allows (the default)." };
var resumeNow = new Option<bool>("--now") { Description = "Resume now even if the last known quota says the limit is on (e.g. after you used a reset credit)." };
var resumeForce = new Option<bool>("--force") { Description = "Resume even though the repository changed since the checkpoint." };
var resume = new Command("resume", "Resume a paused, waiting or blocked job.") { jobId, resumeAt, resumeIn, resumeQuota, resumeNow, resumeForce };
resume.SetAction((parse, ct) => Guarded("resume", () => JobsCommand.ResumeAsync(
    parse.GetValue(jobId)!, parse.GetValue(resumeAt), parse.GetValue(resumeIn), parse.GetValue(resumeQuota), parse.GetValue(resumeNow), parse.GetValue(resumeForce), ct)));
root.Subcommands.Add(resume);

var cancel = new Command("cancel", "Stop supervising a job (the session itself is kept).") { jobId };
cancel.SetAction((parse, ct) => Guarded("cancel", () => JobsCommand.CancelAsync(parse.GetValue(jobId)!, ct)));
root.Subcommands.Add(cancel);

var checkpoint = new Command("checkpoint", "Record branch, HEAD and changes of a job now.") { jobId };
checkpoint.SetAction((parse, ct) => Guarded("checkpoint", () => JobsCommand.CheckpointAsync(parse.GetValue(jobId)!, ct)));
root.Subcommands.Add(checkpoint);

// ── Claude Code integration ────────────────────────────────────────────────────
var claudeAction = new Argument<string>("action") { Description = "status | install | uninstall", DefaultValueFactory = _ => "status" };
claudeAction.AcceptOnlyFromAmong("status", "install", "uninstall");
var claude = new Command("claude", "Fermata inside Claude Code: status line wrapper and hooks.") { claudeAction };
claude.SetAction((parse, ct) => ClaudeCommand.RunAsync(parse.GetValue(claudeAction)!, ct));

var claudeStatusLine = new Command("statusline", "Status line wrapper (installed by `fermata claude install`).") { Hidden = true };
claudeStatusLine.SetAction((_, ct) => ClaudeCommand.StatusLineAsync(ct));
claude.Subcommands.Add(claudeStatusLine);

var claudeHookEvent = new Argument<string>("event") { Description = string.Join(" | ", ClaudeCommand.HookEvents) };
claudeHookEvent.AcceptOnlyFromAmong(ClaudeCommand.HookEvents);
var claudeHook = new Command("hook", "Claude Code hook handler (installed by `fermata claude install`). Never blocks.") { claudeHookEvent };
claudeHook.Hidden = true;
claudeHook.SetAction((parse, ct) => ClaudeCommand.HookAsync(parse.GetValue(claudeHookEvent)!, ct));
claude.Subcommands.Add(claudeHook);
root.Subcommands.Add(claude);

try
{
    return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
}
finally
{
    AppLogging.Shutdown();
}

static async Task<int> Guarded(string command, Func<Task<int>> action)
{
    AppLogging.InitializeFromDefaults();
    var logger = AppLogging.Factory.CreateLogger("Fermata.Cli");
    AppLogging.CommandStarted(logger, command, typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?", System.Runtime.InteropServices.RuntimeInformation.OSDescription);

    int Fail(Exception ex, string code, string message, int exitCode)
    {
        AppLogging.CommandFailed(logger, ex, command, code);
        Console.Error.WriteLine(message);
        AppLogging.CommandFinished(logger, command, exitCode);
        return exitCode;
    }

    try
    {
        var exitCode = await action().ConfigureAwait(false);
        AppLogging.CommandFinished(logger, command, exitCode);
        return exitCode;
    }
    catch (CodexUnavailableException ex)
    {
        return Fail(ex, "CODEX_UNAVAILABLE", $"CODEX_UNAVAILABLE: {ex.Message}", ExitCodes.CodexUnavailable);
    }
    catch (CodexTransientException ex)
    {
        return Fail(ex, "CODEX_UNAVAILABLE", $"CODEX_UNAVAILABLE: {ex.Message}", ExitCodes.CodexUnavailable);
    }
    catch (JsonRpcException ex)
    {
        return Fail(ex, "CODEX_ERROR", $"Codex returned an error: {ex.Message}", ExitCodes.Error);
    }
    catch (CodexProtocolException ex)
    {
        return Fail(ex, "PROTOCOL_ERROR", $"Unexpected Codex response: {ex.Message}", ExitCodes.Error);
    }
    catch (Fermata.Platform.Updates.UpdateException ex)
    {
        return Fail(ex, "UPDATE_FAILED", $"UPDATE_FAILED: {ex.Message}", ExitCodes.Error);
    }
    catch (HttpRequestException ex)
    {
        return Fail(ex, "UPDATE_UNREACHABLE", $"Could not reach the update server: {ex.Message}", ExitCodes.Error);
    }
    catch (FormatException ex)
    {
        return Fail(ex, "BAD_ARGUMENT", ex.Message, ExitCodes.Error);
    }
    catch (Fermata.Core.Jobs.JobProviderException ex)
    {
        return Fail(ex, "PROVIDER_ERROR", ex.Message, ExitCodes.Error);
    }
    catch (MigrationBlockedException ex)
    {
        return Fail(ex, "RESETME_RUNNING", ex.Message, ExitCodes.Error);
    }
    catch (InvalidDataException ex)
    {
        return Fail(ex, "STATE_ERROR", $"State error: {ex.Message}", ExitCodes.Error);
    }
    catch (OperationCanceledException ex)
    {
        return Fail(ex, "CANCELLED", "Cancelled.", ExitCodes.Error);
    }
}

static Task<int> GuardedSync(string command, Func<int> action) => Guarded(command, () => Task.FromResult(action()));
