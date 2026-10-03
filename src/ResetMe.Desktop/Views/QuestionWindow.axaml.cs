using Avalonia.Controls;

namespace ResetMe.Desktop.Views;

/// <summary>Yes/no question; closing the window counts as "cancel".</summary>
public partial class QuestionWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public QuestionWindow()
        : this("", "", "OK", "Cancel")
    {
    }

    public QuestionWindow(string title, string message, string accept, string cancel)
    {
        InitializeComponent();
        Title = $"ResetMe — {title}";
        TitleText.Text = title;
        MessageText.Text = message;
        AcceptButton.Content = accept;
        CancelButton.Content = cancel;
        AcceptButton.Click += (_, _) => Finish(true);
        CancelButton.Click += (_, _) => Finish(false);
    }

    public Task<bool> Result => _result.Task;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        CancelButton.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _result.TrySetResult(false);
        base.OnClosed(e);
    }

    private void Finish(bool accepted)
    {
        _result.TrySetResult(accepted);
        Close();
    }
}
