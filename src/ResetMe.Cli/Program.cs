using System.CommandLine;
using System.Text;
using ResetMe.Cli.Commands;
using ResetMe.Codex.AppServer;
using ResetMe.Codex.JsonRpc;
using ResetMe.Core.Ports;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var root = new RootCommand("ResetMe — watches Codex usage limits and redeems reset credits with your consent.");

var json = new Option<bool>("--json") { Description = "Machine-readable output." };
var status = new Command("status", "Show 5-hour/weekly usage, reset credits and what can be done.") { json };
status.SetAction((parse, ct) => Guarded(() => StatusCommand.RunAsync(parse.GetValue(json), ct)));

var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
var force = new Option<bool>("--force") { Description = "Reset even if the limit lifts soon, the episode was already handled, or the cooldown is active." };
var verbose = new Option<bool>("--verbose", "-v") { Description = "Print state transitions and the raw outcome." };
var reset = new Command("reset", "Redeem one reset credit when Codex is rate-limited.") { yes, force, verbose };
reset.SetAction((parse, ct) => Guarded(() => ResetCommand.RunAsync(
    parse.GetValue(yes), parse.GetValue(force), parse.GetValue(verbose), ct)));

var watch = new Command("watch", "Monitor usage and offer a reset when a limit is reached (Ctrl+C to stop).");
watch.SetAction((_, ct) => Guarded(() => WatchCommand.RunAsync(ct)));

var doctor = new Command("doctor", "Check installation, Codex connectivity and reset capability.");
doctor.SetAction((_, ct) => Guarded(() => DoctorCommand.RunAsync(ct)));

var init = new Option<bool>("--init") { Description = "Create config.toml with defaults if missing." };
var config = new Command("config", "Show the effective configuration.") { init };
config.SetAction(parse => ConfigCommand.Run(parse.GetValue(init)));

root.Subcommands.Add(status);
root.Subcommands.Add(watch);
root.Subcommands.Add(reset);
root.Subcommands.Add(doctor);
root.Subcommands.Add(config);

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> Guarded(Func<Task<int>> action)
{
    try
    {
        return await action().ConfigureAwait(false);
    }
    catch (CodexUnavailableException ex)
    {
        Console.Error.WriteLine($"CODEX_UNAVAILABLE: {ex.Message}");
        return ExitCodes.CodexUnavailable;
    }
    catch (CodexTransientException ex)
    {
        Console.Error.WriteLine($"CODEX_UNAVAILABLE: {ex.Message}");
        return ExitCodes.CodexUnavailable;
    }
    catch (JsonRpcException ex)
    {
        Console.Error.WriteLine($"Codex returned an error: {ex.Message}");
        return ExitCodes.Error;
    }
    catch (CodexProtocolException ex)
    {
        Console.Error.WriteLine($"Unexpected Codex response: {ex.Message}");
        return ExitCodes.Error;
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine($"State error: {ex.Message}");
        return ExitCodes.Error;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        return ExitCodes.Error;
    }
}
