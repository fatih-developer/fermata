using System.CommandLine;
using System.Text;
using Microsoft.Extensions.Logging;
using ResetMe.Cli;
using ResetMe.Cli.Commands;
using ResetMe.Codex.AppServer;
using ResetMe.Codex.JsonRpc;
using ResetMe.Core.Ports;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var root = new RootCommand("ResetMe — watches Codex usage limits and redeems reset credits with your consent.");

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
var autostartPath = new Option<string?>("--path") { Description = "Executable to start (default: the installed app next to resetme)." };
var autostart = new Command("autostart", "Start ResetMe at login.") { autostartAction, autostartTarget, autostartPath };
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
    var logger = AppLogging.Factory.CreateLogger("ResetMe.Cli");
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
    catch (InvalidDataException ex)
    {
        return Fail(ex, "STATE_ERROR", $"State error: {ex.Message}", ExitCodes.Error);
    }
    catch (OperationCanceledException ex)
    {
        return Fail(ex, "CANCELLED", "Cancelled.", ExitCodes.Error);
    }
}
