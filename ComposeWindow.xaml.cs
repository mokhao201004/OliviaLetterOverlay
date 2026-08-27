using System.Windows;
using System.Windows.Input;

namespace OliviaLetterOverlay;

public partial class ComposeWindow : Window
{
    private readonly IReadOnlyList<SavedLetter> _history;

    public event EventHandler<LetterCreatedEventArgs>? LetterCreated;

    public ComposeWindow(IReadOnlyList<SavedLetter> history)
    {
        _history = history;
        InitializeComponent();
        ComposerPaperImage.Source = ComposerPaperRenderer.Render(new Size(830, 478));
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void ClearButton_OnClick(object sender, RoutedEventArgs e)
    {
        DraftBox.Clear();
        StatusText.Visibility = Visibility.Collapsed;
        DraftBox.Focus();
    }

    private async void SendButton_OnClick(object sender, RoutedEventArgs e)
    {
        var draft = DraftBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(draft))
        {
            StatusText.Text = "写点什么再寄出吧。";
            StatusText.Visibility = Visibility.Visible;
            return;
        }

        if (!MimoClient.IsConfigured)
        {
            StatusText.Text = AiProviderStore.MissingConfigurationMessage();
            StatusText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            SendButton.IsEnabled = false;
            SendButton.Content = "等待回信";
            StatusText.Text = "正在生成回信…";
            StatusText.Visibility = Visibility.Visible;
            var reply = await MimoClient.GenerateReplyAsync(draft, _history);
            LetterCreated?.Invoke(this, new LetterCreatedEventArgs(draft, reply));
            Close();
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            StatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            SendButton.IsEnabled = true;
            SendButton.Content = "寄出信件";
        }
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}

public sealed class LetterCreatedEventArgs(string draft, string reply) : EventArgs
{
    public string Draft { get; } = draft;
    public string Reply { get; } = reply;
}
