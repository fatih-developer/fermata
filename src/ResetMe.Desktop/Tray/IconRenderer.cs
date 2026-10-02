using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ResetMe.Desktop.ViewModels;

namespace ResetMe.Desktop.Tray;

/// <summary>
/// Draws the app/tray icon at runtime: a ring filled to the peak usage percent, coloured by health.
/// No image assets and no font dependency.
/// </summary>
public static class IconRenderer
{
    public const int Size = 64;

    public static Color ColorFor(HealthKind health) => health switch
    {
        HealthKind.Ok => Color.Parse("#2E9E5B"),
        HealthKind.Warning => Color.Parse("#D99A1E"),
        HealthKind.Blocked => Color.Parse("#D64545"),
        _ => Color.Parse("#8A8F98"),
    };

    public static Bitmap Render(double percent, HealthKind health)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var center = new Point(Size / 2.0, Size / 2.0);
            const double radius = Size / 2.0 - 7;
            var accent = new SolidColorBrush(ColorFor(health));

            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)), 9), center, radius, radius);

            var fraction = health is HealthKind.Starting or HealthKind.Offline ? 1 : Math.Clamp(percent / 100.0, 0, 1);
            var pen = new Pen(accent, 9, lineCap: PenLineCap.Round);
            if (fraction >= 0.999)
            {
                ctx.DrawEllipse(null, pen, center, radius, radius);
            }
            else if (fraction > 0.01)
            {
                ctx.DrawGeometry(null, pen, Arc(center, radius, fraction));
            }

            ctx.DrawEllipse(accent, null, center, 8, 8);
        }

        return bitmap;
    }

    /// <summary>Clockwise arc starting at 12 o'clock covering <paramref name="fraction"/> of the circle.</summary>
    private static StreamGeometry Arc(Point center, double radius, double fraction)
    {
        var angle = fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, isFilled: false);
            g.ArcTo(end, new Size(radius, radius), 0, isLargeArc: fraction > 0.5, SweepDirection.Clockwise);
            g.EndFigure(isClosed: false);
        }

        return geometry;
    }
}
