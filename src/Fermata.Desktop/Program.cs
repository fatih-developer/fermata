using Avalonia;
using Fermata.Platform;

namespace Fermata.Desktop;

internal static class Program
{
    private const string InstanceName = "desktop";

    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.Default();
        paths.EnsureRoot();

        var instance = SingleInstance.TryAcquire(Path.Combine(paths.Root, "desktop.lock"), InstanceName);
        if (instance is null)
        {
            // Already running (possibly hidden in a tray the desktop does not show): bring it forward.
            SingleInstance.SignalExisting(InstanceName, TimeSpan.FromSeconds(2));
            return 0;
        }

        using (instance)
        {
            App.Startup = new DesktopStartup(args.Contains("--minimized", StringComparer.OrdinalIgnoreCase), instance);
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
