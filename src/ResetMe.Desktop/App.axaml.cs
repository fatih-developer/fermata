using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ResetMe.Codex.AppServer;
using ResetMe.Core.Policies;
using ResetMe.Desktop.Services;
using ResetMe.Desktop.Tray;
using ResetMe.Desktop.ViewModels;
using ResetMe.Desktop.Views;
using ResetMe.Platform;
using ResetMe.Platform.Autostart;
using ResetMe.Platform.Logging;
using ResetMe.Platform.Notifications;

namespace ResetMe.Desktop;

/// <summary>How the process was started (set by <see cref="Program"/> before Avalonia starts).</summary>
public sealed record DesktopStartup(bool Minimized, SingleInstance? Instance);

public partial class App : Application, IDesktopUi
{
    private DesktopHost? _host;
    private TrayController? _tray;
    private MainWindow? _window;
    private ILoggerFactory? _loggers;

    public static DesktopStartup Startup { get; set; } = new(false, null);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var paths = AppPaths.Default();
            paths.EnsureRoot();
            var logging = LoadLoggingOptions(paths);
            _loggers = LoggerFactory.Create(builder => builder
                .SetMinimumLevel(logging.Level)
                .AddProvider(new FileLoggerProvider(paths.LogDirectory, logging.Level, logging.RetentionDays, TimeProvider.System)));

            var connectorOptions = new CodexClientOptions { Executable = new TomlConfigStore(paths.ConfigFile).Load().Options.CodexExecutable };
            _host = new DesktopHost(
                paths,
                new CodexAppServerConnector(connectorOptions, TimeProvider.System, _loggers.CreateLogger("ResetMe.Codex")),
                AutostartFactory.Create(),
                options => NotifierFactory.Create(options.NotificationsEnabled),
                _loggers,
                TimeProvider.System);

            _window = new MainWindow { DataContext = _host.ViewModel };
            UpdateWindowIcon();
            _host.ViewModel.PropertyChanged += OnViewModelChanged;

            _tray = new TrayController(_host.ViewModel, ShowWindow, _host.ChangeModeAsync, () => desktop.Shutdown());
            Startup.Instance?.Listen(() => Dispatcher.UIThread.Post(ShowWindow));
            desktop.Exit += (_, _) => Shutdown();

            _host.Start(this);

            // Autostart passes --minimized; [ui] start_minimized=false still shows the window at login.
            if (!(Startup.Minimized && _host.Options.StartMinimized))
            {
                ShowWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public async Task<bool> ConfirmAsync(ConfirmViewModel content)
    {
        var dialog = new ConfirmWindow { DataContext = content, Icon = _window?.Icon };
        dialog.Show();
        dialog.Activate();
        return await dialog.Result.ConfigureAwait(true);
    }

    public async Task<bool> AskAsync(string title, string message, string accept, string cancel)
    {
        var dialog = new QuestionWindow(title, message, accept, cancel) { Icon = _window?.Icon };
        dialog.Show();
        dialog.Activate();
        return await dialog.Result.ConfigureAwait(true);
    }

    private static LoggingOptions LoadLoggingOptions(AppPaths paths)
    {
        try
        {
            return new TomlConfigStore(paths.ConfigFile).Load().Options.Logging;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Tomlyn.TomlException)
        {
            return new LoggingOptions();
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Health) or nameof(MainViewModel.PeakPercent))
        {
            UpdateWindowIcon();
        }
    }

    private void UpdateWindowIcon()
    {
        if (_window is not null && _host is not null)
        {
            _window.Icon = new WindowIcon(IconRenderer.Render(_host.ViewModel.PeakPercent, _host.ViewModel.Health));
        }
    }

    private void Shutdown()
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
        }

        _tray?.Dispose();
        _host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _loggers?.Dispose();
    }
}
