using CodexResetGuard.Cli.Output;
using CodexResetGuard.Platform;

namespace CodexResetGuard.Cli.Commands;

internal static class ConfigCommand
{
    public static int Run(bool init)
    {
        var paths = AppPaths.Default();
        paths.EnsureRoot();
        var store = new TomlConfigStore(paths.ConfigFile);

        if (init)
        {
            Console.WriteLine(store.EnsureDefault()
                ? $"Created {store.Path}"
                : $"{store.Path} already exists; left unchanged.");
        }

        var result = store.Load();
        var o = result.Options;

        Console.WriteLine($"Config file   {store.Path}{(result.FileExists ? "" : "  (not created; defaults in use)")}");
        Console.WriteLine(Format.Rule);
        Console.WriteLine($"mode                         {Format.Mode(o.Mode)}");
        Console.WriteLine($"monitor.interval_seconds     {o.Monitor.IntervalSeconds}");
        Console.WriteLine($"limits.five_hour / weekly    {o.Limits.FiveHour} / {o.Limits.Weekly}");
        Console.WriteLine($"reset.cooldown_seconds       {o.Reset.CooldownSeconds}");
        Console.WriteLine($"reset.min_time_to_natural    {o.Reset.MinTimeToNaturalResetMinutes} min");
        Console.WriteLine($"reset.verify_timeout_seconds {o.Reset.VerifyTimeoutSeconds}");
        Console.WriteLine($"automatic.max_per_day/week   {o.Automatic.MaxResetsPerDay} / {o.Automatic.MaxResetsPerWeek}");
        Console.WriteLine($"codex.executable             {(o.CodexExecutable.Length == 0 ? "(PATH)" : o.CodexExecutable)}");

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"warning: {warning}");
        }

        return ExitCodes.Ok;
    }
}
