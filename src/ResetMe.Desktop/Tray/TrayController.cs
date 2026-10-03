using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using ResetMe.Core.Policies;
using ResetMe.Desktop.ViewModels;

namespace ResetMe.Desktop.Tray;

/// <summary>
/// Tray icon (Windows), menu bar extra (macOS) or StatusNotifierItem (Linux) with the menu of
/// PRD §37. Kept in sync with <see cref="MainViewModel"/>.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly TrayIcon _tray;
    private readonly NativeMenuItem _fiveHour = new() { IsEnabled = false };
    private readonly NativeMenuItem _weekly = new() { IsEnabled = false };
    private readonly NativeMenuItem _credits = new() { IsEnabled = false };
    private readonly NativeMenuItem _status = new() { IsEnabled = false };
    private readonly NativeMenuItem _resetNow = new("Reset now…");
    private readonly NativeMenuItem _update = new() { IsVisible = false };
    private readonly NativeMenuItem[] _modes;
    private HealthKind _iconHealth = (HealthKind)(-1);
    private int _iconBucket = -1;

    public TrayController(MainViewModel vm, Action showWindow, Func<GuardMode, Task> changeMode, Action quit)
    {
        _vm = vm;
        _resetNow.Click += (_, _) => vm.ResetNowCommand.Execute(null);
        _update.Click += (_, _) =>
        {
            if (vm.InstallUpdateCommand.CanExecute(null))
            {
                vm.InstallUpdateCommand.Execute(null);
            }
            else
            {
                showWindow();
            }
        };

        _modes = [.. Enum.GetValues<GuardMode>().Select(mode =>
        {
            var item = new NativeMenuItem(MainViewModel.ModeNames[(int)mode]) { ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) => _ = changeMode(mode);
            return item;
        })];

        var open = new NativeMenuItem("Open ResetMe");
        open.Click += (_, _) => showWindow();
        var logs = new NativeMenuItem("Open logs");
        logs.Click += (_, _) => vm.OpenLogsCommand.Execute(null);
        var exit = new NativeMenuItem("Quit ResetMe");
        exit.Click += (_, _) => quit();

        var modeMenu = new NativeMenu();
        foreach (var item in _modes)
        {
            modeMenu.Items.Add(item);
        }

        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem("ResetMe") { IsEnabled = false });
        menu.Items.Add(_status);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_fiveHour);
        menu.Items.Add(_weekly);
        menu.Items.Add(_credits);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_resetNow);
        menu.Items.Add(_update);
        menu.Items.Add(new NativeMenuItem("Mode") { Menu = modeMenu });
        menu.Items.Add(open);
        menu.Items.Add(logs);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        Menu = menu;
        _tray = new TrayIcon { Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => showWindow();

        vm.PropertyChanged += OnViewModelChanged;
        Refresh();

        TrayIcon.SetIcons(Application.Current!, [_tray]);
    }

    internal NativeMenu Menu { get; }

    internal string? ToolTip => _tray.ToolTipText;

    public void Dispose()
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        _tray.Dispose();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        _fiveHour.Header = $"5-hour usage     {_vm.FiveHourText}  ({_vm.FiveHourLeftText})";
        _weekly.Header = $"Weekly usage     {_vm.WeeklyText}  ({_vm.WeeklyLeftText})";
        _credits.Header = $"Reset credits     {_vm.CreditsText}";
        _status.Header = _vm.StatusText;
        _resetNow.IsEnabled = _vm.ResetNowCommand.CanExecute(null);
        for (var i = 0; i < _modes.Length; i++)
        {
            _modes[i].IsChecked = i == _vm.ModeIndex;
        }

        _tray.ToolTipText = _vm.TrayToolTip;
        _update.IsVisible = _vm.UpdateAvailable;
        _update.Header = $"Install ResetMe {_vm.UpdateVersion}…";

        // Redraw only when the visible state changes (5% steps).
        var bucket = (int)(_vm.PeakPercent / 5);
        if (bucket != _iconBucket || _vm.Health != _iconHealth)
        {
            _iconBucket = bucket;
            _iconHealth = _vm.Health;
            _tray.Icon = new WindowIcon(IconRenderer.Render(_vm.PeakPercent, _vm.Health));
        }
    }
}
