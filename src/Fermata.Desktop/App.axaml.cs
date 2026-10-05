using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Fermata.Codex.AppServer;
using Fermata.Core.Policies;
using Fermata.Desktop.Services;
using Fermata.Desktop.Tray;
using Fermata.Desktop.ViewModels;
using Fermata.Desktop.Views;
using Fermata.Platform;
using Fermata.Platform.Autostart;
using Fermata.Platform.Logging;
using Fermata.Platform.Notifications;

namespace Fermata.Desktop;

/// <summary>How the process was started (set by <see cref="Program"/> before Avalonia starts).</summary>
public sealed record DesktopStartup(bool Minimized, SingleInstance? Instance);

public partial class App : Application, IDesktopUi
{
    private DesktopHost? _host;
    private TrayController? _tray;
    private MainWindow? _window;
    private FlyoutWindow? _flyout;
    private ILoggerFactory? _loggers;

    public static DesktopStartup Startup { get; set; } = new(false, null);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.UIThread.Post(async () => await StartAsync(desktop).ConfigureAwait(true));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var paths = AppPaths.Default();
        var migration = await MigrateAsync(paths).ConfigureAwait(true);
        if (migration is null)
        {
            desktop.Shutdown();
            return;
        }

        paths.EnsureRoot();
        var logging = LoadLoggingOptions(paths);
        _loggers = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(logging.Level)
            .AddProvider(new FileLoggerProvider(paths.LogDirectory, logging.Level, logging.RetentionDays, TimeProvider.System)));

        var connectorOptions = new CodexClientOptions { Executable = new TomlConfigStore(paths.ConfigFile).Load().Options.CodexExecutable };
        _host = new DesktopHost(
            paths,
            new CodexAppServerConnector(connectorOptions, TimeProvider.System, _loggers.CreateLogger("Fermata.Codex")),
            AutostartFactory.Create(),
            options => NotifierFactory.Create(options.NotificationsEnabled),
            _loggers,
            TimeProvider.System,
            new GitHubUpdateService(),
            options => DesktopJobProviders.Create(paths, options));
        _host.RequestExit = () => Dispatcher.UIThread.Post(() => desktop.Shutdown());

        _window = new MainWindow { DataContext = _host.ViewModel };
        UpdateWindowIcon();
        _host.ViewModel.PropertyChanged += OnViewModelChanged;

        _flyout = new FlyoutWindow { DataContext = _host.ViewModel, ShowDetails = ShowWindow };
        _tray = new TrayController(_host.ViewModel, ShowWindow, _host.ChangeModeAsync, () => desktop.Shutdown(), _flyout.Toggle);
        Startup.Instance?.Listen(() => Dispatcher.UIThread.Post(ShowWindow));
        desktop.Exit += (_, _) => Shutdown();

        _host.Start(this);
        if (migration.Outcome == MigrationOutcome.Migrated)
        {
            await _host.CompleteMigrationAsync(migration).ConfigureAwait(true);
        }

        // Autostart passes --minimized; [ui] start_minimized=false still shows the window at login.
        if (!(Startup.Minimized && _host.Options.StartMinimized))
        {
            ShowWindow();
        }
    }

    /// <summary>
    /// Copies ResetMe's data on the first start after the rename. Null means quit: ResetMe still
    /// runs and the user chose not to retry.
    /// </summary>
    private async Task<MigrationResult?> MigrateAsync(AppPaths paths)
    {
        var migration = LegacyMigration.ForDefaultPaths(paths);
        while (true)
        {
            var result = await migration.RunAsync(CancellationToken.None).ConfigureAwait(true);
            if (result.Outcome != MigrationOutcome.BlockedByRunningResetMe)
            {
                return result;
            }

            if (!await AskAsync("Quit ResetMe first", MigrationResult.BlockedMessage, "Try again", "Quit Fermata").ConfigureAwait(true))
            {
                return null;
            }
        }
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

        _flyout?.Close();
        _tray?.Dispose();
        _host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _loggers?.Dispose();
    }
}
