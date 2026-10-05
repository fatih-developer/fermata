using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Fermata.Core.Monitoring;
using Fermata.Core.Policies;
using Fermata.Desktop.Tray;
using Fermata.Desktop.ViewModels;
using Fermata.Desktop.Views;
using Xunit;

namespace Fermata.Desktop.Tests;

/// <summary>Renders the real windows headlessly and saves PNGs for visual review.</summary>
public class RenderingTests
{
    [AvaloniaFact]
    public void Main_window_renders_the_blocked_state()
    {
        var vm = new MainViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => { }) { TimeZone = TimeZoneInfo.Utc };
        var usage = Fixtures.Blocked();
        vm.ApplyUsage(usage, LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now), Fixtures.Now);
        vm.AddEvent("5-hour Codex limit reached. 2 reset credit(s) available. Open Fermata to use one.", Fixtures.Now);
        vm.AddEvent("Connected to Codex.", Fixtures.Now.AddMinutes(-30));

        var window = new MainWindow { DataContext = vm, Width = 440, Height = 680 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = Save(window.CaptureRenderedFrame(), "main-window-blocked.png");
        Assert.Equal(440, frame.PixelSize.Width);
        window.AllowClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void Main_window_renders_the_healthy_state()
    {
        var vm = new MainViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => { }) { TimeZone = TimeZoneInfo.Utc };
        var usage = Fixtures.Healthy(62, 41);
        vm.ApplyUsage(usage, LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now), Fixtures.Now);

        var window = new MainWindow { DataContext = vm, Width = 440, Height = 680 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Save(window.CaptureRenderedFrame(), "main-window-healthy.png");
        window.AllowClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void Main_window_renders_the_settings_panel()
    {
        var vm = new MainViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => { }) { TimeZone = TimeZoneInfo.Utc };
        var usage = Fixtures.Healthy(62, 41);
        vm.ApplyUsage(usage, LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now), Fixtures.Now);
        vm.LoadSettings(new GuardOptions(), startAtLogin: true);

        var window = new MainWindow { DataContext = vm, Width = 440, Height = 960 };
        window.Show();
        window.GetVisualDescendants().OfType<Avalonia.Controls.Expander>().Single().IsExpanded = true;
        Dispatcher.UIThread.RunJobs();

        Save(window.CaptureRenderedFrame(), "main-window-settings.png");
        window.AllowClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void Tray_menu_mirrors_the_view_model()
    {
        var vm = new MainViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => { }) { TimeZone = TimeZoneInfo.Utc };
        using var tray = new TrayController(vm, () => { }, _ => Task.CompletedTask, () => { });

        var usage = Fixtures.Blocked();
        vm.ApplyUsage(usage, LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now), Fixtures.Now);
        vm.ModeIndex = (int)GuardMode.Automatic;

        var items = tray.Menu.Items.OfType<Avalonia.Controls.NativeMenuItem>().ToList();
        Assert.Contains(items, i => i.Header == "5-hour usage     100%  (0% left)");
        Assert.Contains(items, i => i.Header == "Reset credits     2");
        Assert.Contains(items, i => i.Header == "Limit reached");
        Assert.True(items.Single(i => i.Header == "Reset now…").IsEnabled);

        var modes = items.Single(i => i.Header == "Mode").Menu!.Items.OfType<Avalonia.Controls.NativeMenuItem>().ToList();
        Assert.Equal(["Automatic"], modes.Where(m => m.IsChecked).Select(m => m.Header));
        Assert.Contains("5h 100%", tray.ToolTip, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Flyout_renders_compactly(bool blocked)
    {
        var vm = new MainViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => { }) { TimeZone = TimeZoneInfo.Utc };
        var usage = blocked ? Fixtures.Blocked() : Fixtures.Healthy(62, 41);
        vm.ApplyUsage(usage, LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now), Fixtures.Now);
        vm.LoadSettings(new GuardOptions { Mode = blocked ? GuardMode.Confirm : GuardMode.Automatic }, startAtLogin: false);

        var flyout = new FlyoutWindow { DataContext = vm };
        flyout.Show();
        Dispatcher.UIThread.RunJobs();

        var frame = Save(flyout.CaptureRenderedFrame(), blocked ? "flyout-blocked.png" : "flyout-healthy.png");
        Assert.Equal(320, frame.PixelSize.Width);
        Assert.InRange(frame.PixelSize.Height, 120, 260);
        Assert.Equal(blocked ? "2h 40m" : "3h 0m", vm.FiveHourResetsInText);
        flyout.Close();
    }

    [AvaloniaFact]
    public void Confirm_window_renders_and_defaults_to_wait()
    {
        var usage = Fixtures.Blocked();
        var assessment = LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now);
        var dialog = new ConfirmWindow { DataContext = new ConfirmViewModel(new LimitNotice(usage, assessment, LimitHandling.AskUser), Fixtures.Now) };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        Save(dialog.CaptureRenderedFrame(), "confirm-window.png");

        dialog.Close(); // closing without a click means "wait"
        Dispatcher.UIThread.RunJobs();
        Assert.True(dialog.Result.IsCompleted);
        Assert.False(dialog.Result.Result);
    }

    [AvaloniaFact]
    public void Automatic_mode_question_renders()
    {
        var dialog = new QuestionWindow(
            "Enable automatic mode?",
            "In automatic mode Fermata redeems a reset credit as soon as Codex reports a limit, without asking you.\n\nSafeguards that still apply:\n• at most 1 per day and 2 per week\n• 120s cooldown between attempts",
            "Enable automatic mode",
            "Cancel");
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        Save(dialog.CaptureRenderedFrame(), "automatic-mode-question.png");
        dialog.Close();
        Assert.False(dialog.Result.Result);
    }

    [AvaloniaTheory]
    [InlineData(HealthKind.Ok, 40)]
    [InlineData(HealthKind.Warning, 85)]
    [InlineData(HealthKind.Blocked, 100)]
    [InlineData(HealthKind.Offline, 0)]
    public void Tray_icon_is_drawn_in_the_health_colour(HealthKind health, double percent)
    {
        using var icon = IconRenderer.Render(percent, health);
        Save(icon, $"tray-{health.ToString().ToLowerInvariant()}.png");

        // The centre dot always carries the health colour. The platform default is BGRA on
        // Windows/Linux and RGBA on macOS, so accept either channel order.
        var expected = IconRenderer.ColorFor(health);
        var (c0, c1, c2) = ReadPixel(icon, IconRenderer.Size / 2, IconRenderer.Size / 2);
        static bool Near(byte a, byte b) => Math.Abs(a - b) <= 3;
        var bgra = Near(c2, expected.R) && Near(c1, expected.G) && Near(c0, expected.B);
        var rgba = Near(c0, expected.R) && Near(c1, expected.G) && Near(c2, expected.B);
        Assert.True(bgra || rgba, $"centre pixel {c0},{c1},{c2} is not {expected}");
    }

    private static Bitmap Save(Bitmap? bitmap, string name)
    {
        Assert.NotNull(bitmap);
        bitmap.Save(Path.Combine(Fixtures.ScreenshotDirectory(), name), PngBitmapEncoderOptions.Default);
        return bitmap;
    }

    /// <summary>First three bytes of the pixel, in the bitmap's native channel order.</summary>
    private static (byte C0, byte C1, byte C2) ReadPixel(Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        unsafe
        {
            fixed (byte* p = buffer)
            {
                bitmap.CopyPixels(new Avalonia.PixelRect(x, y, 1, 1), (nint)p, buffer.Length, 4);
            }
        }

        return (buffer[0], buffer[1], buffer[2]);
    }
}
