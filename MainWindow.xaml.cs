using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OliviaLetterOverlay;

public partial class MainWindow : Window
{
    private readonly string _helloReply = "你好。\n\n收到你的信了。虽然只有简单的两个字，但我还是把它读了两遍。傍晚的琴房安静得有点过分，窗外的光落在琴键上，我刚好把一张旧唱片翻到另一面。\n\n不必急着把每件事都写得很完整。你想说什么，就慢一点写给我。\n\n—— 林离";
    private const int BuiltInLetterCount = 3;
    private readonly List<SavedLetter> _savedLetters = [];
    private readonly List<Button> _savedLetterItems = [];
    private string _currentDraft = string.Empty;
    private string _currentReply = string.Empty;
    private string _currentDate = "2026-08-26 18:08";
    private string _currentSubject = "你好";
    private BitmapSource? _currentReplyImage;
    private BitmapSource? _currentSentImage;
    private readonly DispatcherTimer _autoLetterTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _gameWatchTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _isGeneratingAutoLetter;

    public MainWindow()
    {
        InitializeComponent();
        ReloadSavedLetters();
        if (_savedLetters.Count > 0)
        {
            var newest = _savedLetters[0];
            ShowLetter(newest.Subject, FormatDate(newest.CreatedAt), newest.Draft, newest.Reply);
            SelectItem(_savedLetterItems[0]);
        }
        else
        {
            ShowLetter("你好", "2026-08-26 18:08", "你好", _helloReply);
            SelectItem(HelloItem);
        }

        _autoLetterTimer.Tick += AutoLetterTimer_OnTick;
        _autoLetterTimer.Start();
        Loaded += async (_, _) => await TryGenerateAutoLetterAsync();
        Closed += (_, _) => _autoLetterTimer.Stop();

        if (Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "--watch", StringComparison.OrdinalIgnoreCase)))
        {
            Visibility = Visibility.Hidden;
            ShowInTaskbar = false;
            _gameWatchTimer.Tick += GameWatchTimer_OnTick;
            _gameWatchTimer.Start();
        }
    }

    private void GameWatchTimer_OnTick(object? sender, EventArgs e)
    {
        var game = Process.GetProcessesByName("Olivia").FirstOrDefault(process => process.MainWindowHandle != IntPtr.Zero);
        if (game is null)
        {
            return;
        }

        _gameWatchTimer.Stop();
        ShowInTaskbar = true;
        Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void AutoLetterTimer_OnTick(object? sender, EventArgs e) => await TryGenerateAutoLetterAsync();

    private async Task TryGenerateAutoLetterAsync()
    {
        if (_isGeneratingAutoLetter || !MimoClient.IsConfigured)
        {
            return;
        }

        var settings = AutoLetterStore.Load();
        if (settings.IntervalMinutes < 10 || (settings.LastSentAt is not null && DateTime.Now - settings.LastSentAt.Value < TimeSpan.FromMinutes(settings.IntervalMinutes)))
        {
            return;
        }

        try
        {
            _isGeneratingAutoLetter = true;
            var reply = await MimoClient.GenerateProactiveLetterAsync(_savedLetters);
            var sentAt = DateTime.Now;
            var letter = new SavedLetter
            {
                CreatedAt = sentAt,
                Subject = "林离的来信",
                Draft = "林离主动寄来的一封信",
                Reply = reply,
                IsAutoLetter = true,
            };

            _savedLetters.Insert(0, letter);
            LetterStore.Save(_savedLetters);
            settings.LastSentAt = sentAt;
            AutoLetterStore.Save(settings);

            var item = AddSavedLetterItem(letter, insertAtTop: true);
            UpdateMailboxTitle();
            ShowLetter(letter.Subject, FormatDate(letter.CreatedAt), letter.Draft, letter.Reply);
            SelectItem(item);
        }
        catch
        {
            // An automatic letter is optional; preserve the current mailbox if the request or local save fails.
        }
        finally
        {
            _isGeneratingAutoLetter = false;
        }
    }

    internal void ImportReferenceLetters(IReadOnlyList<PersonaReferenceLetter> references)
    {
        var storedLetters = LetterStore.Load();
        var importedCount = 0;
        foreach (var reference in references
                     .Where(item => !string.IsNullOrWhiteSpace(item.Draft) && !string.IsNullOrWhiteSpace(item.Reply))
                     .GroupBy(item => (item.Draft.Trim(), item.Reply.Trim()))
                     .Select(group => group.First()))
        {
            if (storedLetters.Any(item => item.IsReference && item.Draft == reference.Draft && item.Reply == reference.Reply))
            {
                continue;
            }

            var subject = string.IsNullOrWhiteSpace(reference.Subject) ? FirstLine(reference.Draft) : reference.Subject.Trim();
            storedLetters.Add(new SavedLetter
            {
                CreatedAt = DateTime.Now.AddMilliseconds(importedCount),
                Subject = "参考｜" + subject,
                Draft = reference.Draft.Trim(),
                Reply = reference.Reply.Trim(),
                IsReference = true,
            });
            importedCount++;
        }

        if (importedCount == 0)
        {
            return;
        }

        LetterStore.Save(storedLetters);
        ReloadSavedLetters();
        var newest = _savedLetters[0];
        ShowLetter(newest.Subject, FormatDate(newest.CreatedAt), newest.Draft, newest.Reply);
        SelectItem(_savedLetterItems[0]);
    }

    private void ReloadSavedLetters()
    {
        _savedLetters.Clear();
        _savedLetters.AddRange(LetterStore.Load().OrderByDescending(letter => letter.CreatedAt));
        _savedLetterItems.Clear();
        SavedLettersPanel.Children.Clear();
        foreach (var letter in _savedLetters)
        {
            AddSavedLetterItem(letter, insertAtTop: false);
        }

        UpdateMailboxTitle();
    }

    private void ComposeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var compose = new ComposeWindow(_savedLetters) { Owner = this };
        compose.LetterCreated += (_, args) =>
        {
            var subject = FirstLine(args.Draft);
            var letter = new SavedLetter
            {
                CreatedAt = DateTime.Now,
                Subject = subject,
                Draft = args.Draft,
                Reply = args.Reply,
            };

            try
            {
                _savedLetters.Insert(0, letter);
                LetterStore.Save(_savedLetters);
            }
            catch (IOException)
            {
                _savedLetters.Remove(letter);
                MessageBox.Show(this, "这封信暂时无法写入本机信箱。请确认磁盘可写后再试。", "未能保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var item = AddSavedLetterItem(letter, insertAtTop: true);
            UpdateMailboxTitle();
            ShowLetter(letter.Subject, FormatDate(letter.CreatedAt), letter.Draft, letter.Reply);
            SelectItem(item);
        };
        compose.ShowDialog();
    }

    private void ApiSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var settings = new ApiSettingsWindow { Owner = this };
        settings.ShowDialog();
    }

    private void MailboxItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button item)
        {
            return;
        }

        SelectItem(item);
        var title = item.Tag as string ?? "你好";
        if (title == "欢迎来到信箱")
        {
            ShowLetter(title, "2026-08-26 17:32", "欢迎来到信箱", "你好。\n\n这里会收下你想慢慢写的事。信不需要写得漂亮，也不用急着讲完；有些话像最后几个音，先欠着也没有关系。\n\n窗外有一点风，我刚把练习本合上。想说的话，可以写给我。\n\n—— 林离");
        }
        else
        {
            ShowLetter("你好", "2026-08-26 18:08", "你好", _helloReply);
        }
    }

    private void ShowLetter(string subject, string date, string sentText, string reply)
    {
        SubjectText.Text = subject;
        DateText.Text = date;
        _currentSubject = subject;
        _currentDate = date;
        _currentDraft = sentText;
        _currentReply = reply;
        _currentReplyImage = ReplyLetterRenderer.Render(reply, new Size(554, 310));
        _currentSentImage = SentLetterRenderer.Render(sentText, new Size(554, 310), date[..10]);
        ReplyImage.Source = _currentReplyImage;
        SentImage.Source = _currentSentImage;
    }

    private async void DownloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentReplyImage is null || _currentSentImage is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            AddExtension = false,
            FileName = $"{LetterExport.SafeFileName(_currentSubject)}-{_currentDate[..10]}",
            Filter = "PNG 图像（输入文件名前缀）|*.png",
            Title = "选择两张信件图片的保存前缀",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LetterExport.SavePair(_currentSentImage, _currentReplyImage, dialog.FileName);
        await FlashLabelAsync(DownloadButtonLabel, "已存两张", "下载");
    }

    private async void ShareButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentReplyImage is null || _currentSentImage is null)
        {
            return;
        }

        var data = new DataObject();
        data.SetImage(LetterExport.Combine(_currentReplyImage, _currentSentImage));
        data.SetText($"{_currentSubject}\n{_currentDate}\n\n{_currentReply}", TextDataFormat.UnicodeText);
        Clipboard.SetDataObject(data, true);
        await FlashLabelAsync(ShareButtonLabel, "已复制", "分享信件");
    }

    private static async Task FlashLabelAsync(TextBlock label, string message, string original)
    {
        label.Text = message;
        await Task.Delay(1600);
        label.Text = original;
    }

    private void SelectItem(Button selected)
    {
        foreach (var item in _savedLetterItems.Concat(new[] { HelloItem, WelcomeItem }))
        {
            item.Background = item == selected ? new SolidColorBrush(Color.FromRgb(42, 45, 50)) : Brushes.Transparent;
        }
    }

    private Button AddSavedLetterItem(SavedLetter letter, bool insertAtTop)
    {
        var item = new Button
        {
            Style = (Style)FindResource("MailboxItem"),
            Margin = new Thickness(0, 4, 0, 0),
            Background = Brushes.Transparent,
        };

        var layout = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        layout.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(letter.IsReference || letter.IsAutoLetter ? Color.FromRgb(85, 122, 106) : Color.FromRgb(116, 109, 98)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = letter.IsReference || letter.IsAutoLetter ? "\uE8F1" : "\uE724",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(242, 240, 236)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });

        var details = new StackPanel
        {
            Margin = new Thickness(52, 8, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        details.Children.Add(new TextBlock
        {
            Text = letter.Subject,
            Foreground = new SolidColorBrush(Color.FromRgb(240, 238, 235)),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        details.Children.Add(new TextBlock
        {
            Text = FormatDate(letter.CreatedAt),
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(155, 158, 165)),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 11,
        });
        layout.Children.Add(details);
        item.Content = layout;
        item.Click += (_, _) =>
        {
            ShowLetter(letter.Subject, FormatDate(letter.CreatedAt), letter.Draft, letter.Reply);
            SelectItem(item);
        };

        if (insertAtTop)
        {
            SavedLettersPanel.Children.Insert(0, item);
            _savedLetterItems.Insert(0, item);
        }
        else
        {
            SavedLettersPanel.Children.Add(item);
            _savedLetterItems.Add(item);
        }

        return item;
    }

    private void UpdateMailboxTitle() => MailboxTitleText.Text = $"我的信箱  {_savedLetters.Count + BuiltInLetterCount}";

    private static string FormatDate(DateTime date) => date.ToString("yyyy-MM-dd HH:mm");

    private static string FirstLine(string value)
    {
        var text = value.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "写给林离";
        return text.Length > 24 ? text[..24] + "…" : text;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

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















