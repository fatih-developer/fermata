using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Fermata.Desktop.Views;

/// <summary>
/// Reset confirmation. "Wait" is the default and the cancel action; closing the window also means
/// "wait", so a credit is only used after an explicit click on "Use reset credit".
/// </summary>
public partial class ConfirmWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfirmWindow()
    {
        InitializeComponent();
        UseButton.Click += OnUse;
        WaitButton.Click += OnWait;
    }

    public Task<bool> Result => _result.Task;

    protected override void OnClosed(EventArgs e)
    {
        _result.TrySetResult(false);
        base.OnClosed(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WaitButton.Focus();
    }

    private void OnUse(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(true);
        Close();
    }

    private void OnWait(object? sender, RoutedEventArgs e)
    {
        _result.TrySetResult(false);
        Close();
    }
}
