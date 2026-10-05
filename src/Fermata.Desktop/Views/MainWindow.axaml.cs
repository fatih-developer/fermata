using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Fermata.Desktop.Tray;
using Fermata.Desktop.ViewModels;

namespace Fermata.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>When true the window really closes (app exit); otherwise closing hides it to the tray.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}

public sealed class HealthBrushConverter : IValueConverter
{
    public static readonly HealthBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(IconRenderer.ColorFor(value is HealthKind health ? health : HealthKind.Starting));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Usage bar colour: same traffic light as the tray icon.</summary>
public sealed class PercentBrushConverter : IValueConverter
{
    public static readonly PercentBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value is double d ? d : 0;
        var health = percent >= 100 ? HealthKind.Blocked : percent >= MainViewModel.WarningPercent ? HealthKind.Warning : HealthKind.Ok;
        return new SolidColorBrush(IconRenderer.ColorFor(health));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
