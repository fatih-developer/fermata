using System.Diagnostics;
using ResetMe.Cli.Output;
using ResetMe.Platform.Updates;

namespace ResetMe.Cli.Commands;

/// <summary>`resetme update [--check] [--yes]` (PRD MVP-3 self-update).</summary>
internal static class UpdateCommand
{
    public static async Task<int> RunAsync(bool checkOnly, bool yes, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var current = UpdateChecker.CurrentVersion();
        var packageName = UpdateChecker.PackageNameFor(UpdateChecker.CurrentRuntimeIdentifier());
        var check = await new UpdateChecker(http).CheckAsync(current, packageName, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"Installed  {current}");
        Console.WriteLine($"Latest     {check.Latest.Version}   {check.Latest.PageUrl}");
        if (!check.UpdateAvailable)
        {
            Console.WriteLine("ResetMe is up to date.");
            return ExitCodes.Ok;
        }

        if (checkOnly)
        {
            Console.WriteLine("Run `resetme update` to install it.");
            return ExitCodes.Ok;
        }

        var layout = InstallLayout.Detect(AppContext.BaseDirectory);
        if (layout is null)
        {
            Console.WriteLine("This copy of ResetMe is not an installed package (development build); it is never replaced.");
            Console.WriteLine($"Download the new version from {check.Latest.PageUrl}");
            return ExitCodes.Error;
        }

        if (check.Package is null)
        {
            Console.WriteLine($"Release {check.Latest.Tag} has no package for this platform; see {check.Latest.PageUrl}");
            return ExitCodes.Error;
        }

        var others = OtherResetMeProcesses(layout);
        if (OperatingSystem.IsWindows() && others > 0)
        {
            Console.WriteLine("The ResetMe app is running from this folder and Windows cannot replace running files.");
            Console.WriteLine("Quit it first (tray icon › Quit ResetMe), or use \"Install update\" inside the app.");
            return ExitCodes.Error;
        }

        if (!ResetPresenter.Confirm(yes, $"Install ResetMe {check.Latest.Version} into {layout.Root}? [y/N] "))
        {
            return ExitCodes.Declined;
        }

        var staged = await new UpdateInstaller(http)
            .DownloadAndStageAsync(check, layout, new ConsoleProgress(), cancellationToken)
            .ConfigureAwait(false);

        switch (UpdateInstaller.Apply(staged, layout, [Environment.ProcessId], relaunchDesktop: false))
        {
            case ApplyResult.Applied:
                Console.WriteLine($"Updated to {check.Latest.Version}.");
                if (others > 0)
                {
                    Console.WriteLine("Restart the ResetMe app (and any `resetme daemon`) to use the new version.");
                }

                break;

            case ApplyResult.Scheduled:
                Console.WriteLine($"ResetMe {check.Latest.Version} is installed as soon as this command exits (a few seconds).");
                break;
        }

        return ExitCodes.Ok;
    }

    /// <summary>Other ResetMe processes (tray app, daemon) running from the install folder.</summary>
    private static int OtherResetMeProcesses(InstallLayout layout)
    {
        var count = 0;
        foreach (var name in new[] { "ResetMeApp", "resetme" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.Id != Environment.ProcessId
                            && process.MainModule?.FileName is { } path
                            && path.StartsWith(layout.BinDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            count++;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Exited meanwhile or not ours to inspect.
                    }
                }
            }
        }

        return count;
    }

    /// <summary>Synchronous progress (Progress&lt;T&gt; would reorder lines on the thread pool).</summary>
    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string value) => Console.WriteLine(value);
    }
}
