using Avalonia;
using Fermata.Desktop.Views;
using Xunit;

namespace Fermata.Desktop.Tests;

public class FlyoutPlacementTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);
    private static readonly PixelSize Flyout = new(320, 200);

    [Fact]
    public void Bottom_taskbar_puts_the_flyout_above_it_on_the_right()
    {
        var position = FlyoutPlacement.Compute(Screen, new PixelRect(0, 0, 1920, 1032), Flyout, 12);

        Assert.Equal(new PixelPoint(1920 - 320 - 12, 1032 - 200 - 12), position);
    }

    [Fact]
    public void Top_menu_bar_puts_the_flyout_below_it_on_the_right()
    {
        var position = FlyoutPlacement.Compute(Screen, new PixelRect(0, 25, 1920, 1055), Flyout, 12);

        Assert.Equal(new PixelPoint(1920 - 320 - 12, 25 + 12), position);
    }

    [Fact]
    public void Left_taskbar_puts_the_flyout_next_to_it_at_the_bottom()
    {
        var position = FlyoutPlacement.Compute(Screen, new PixelRect(60, 0, 1860, 1080), Flyout, 12);

        Assert.Equal(new PixelPoint(60 + 12, 1080 - 200 - 12), position);
    }

    [Fact]
    public void Right_taskbar_puts_the_flyout_next_to_it_at_the_bottom()
    {
        var position = FlyoutPlacement.Compute(Screen, new PixelRect(0, 0, 1860, 1080), Flyout, 12);

        Assert.Equal(new PixelPoint(1860 - 320 - 12, 1080 - 200 - 12), position);
    }
}
