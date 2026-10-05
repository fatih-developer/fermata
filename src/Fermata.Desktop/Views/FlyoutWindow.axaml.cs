using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Fermata.Desktop.Views;

/// <summary>
/// The small tray / menu bar panel: usage, credits, auto-reset switch and "Reset now". Opens next to
/// the taskbar, hides when it loses focus. Details and settings stay in <see cref="MainWindow"/>.
/// </summary>
public partial class FlyoutWindow : Window
{
    // A click on the tray icon first deactivates the open flyout; without this the same click
    // would reopen it immediately.
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);
    private DateTimeOffset _hiddenAt = DateTimeOffset.MinValue;

    public FlyoutWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) => HideFlyout();
        DetailsButton.Click += OnDetailsClick;
    }

    /// <summary>Opens the main window (set by the app).</summary>
    public Action ShowDetails { get; set; } = () => { };

    /// <summary>Tray click: open if hidden, close if open.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            HideFlyout();
            return;
        }

        if (DateTimeOffset.UtcNow - _hiddenAt < ReopenGuard)
        {
            return;
        }

        Show();
        PlaceNearTaskbar();
        Activate();
    }

    private void HideFlyout()
    {
        if (IsVisible)
        {
            _hiddenAt = DateTimeOffset.UtcNow;
            Hide();
        }
    }

    private void OnDetailsClick(object? sender, RoutedEventArgs e)
    {
        HideFlyout();
        ShowDetails();
    }

    private void PlaceNearTaskbar()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var size = PixelSize.FromSize(Bounds.Size, screen.Scaling);
        Position = FlyoutPlacement.Compute(screen.Bounds, screen.WorkingArea, size, (int)(12 * screen.Scaling));
    }
}

/// <summary>Where the flyout goes: the screen corner next to the taskbar / menu bar.</summary>
public static class FlyoutPlacement
{
    /// <summary>
    /// The taskbar is the edge where the working area is smaller than the screen. The flyout hugs
    /// that edge on the right side (or the left/right taskbar's bottom end). No taskbar: bottom right.
    /// </summary>
    public static PixelPoint Compute(PixelRect screen, PixelRect working, PixelSize flyout, int margin)
    {
        var right = working.Right - flyout.Width - margin;
        var bottom = working.Bottom - flyout.Height - margin;

        if (working.Y > screen.Y)
        {
            return new PixelPoint(right, working.Y + margin); // top (macOS menu bar, top taskbar)
        }

        if (working.X > screen.X)
        {
            return new PixelPoint(working.X + margin, bottom); // left taskbar
        }

        return new PixelPoint(right, bottom); // bottom or right taskbar, or none
    }
}
