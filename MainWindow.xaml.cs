using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
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
    private string? _currentLetterKey;
    private Dictionary<string, string> _letterTitles = [];
    private IReadOnlyList<BitmapSource> _currentReplyPages = [];
    private BitmapSource? _currentSentImage;
    private readonly DispatcherTimer _autoLetterTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _gameWatchTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _isGeneratingAutoLetter;
    private bool _isSettingsOpen;
    private bool _userHiddenByHotkey;
    private string _characterId = CharacterStore.Current.Id;
    private readonly IndexTtsClient _ttsClient = new();
    private SpeechPhase _speechPhase = SpeechPhase.Idle;
    private CancellationTokenSource? _readAloudCts;
    private System.Windows.Media.MediaPlayer? _speechPlayer;
    private TextBlock? _activeSpeechLabel;

    private enum SpeechPhase
    {
        Idle,
        Generating,
        Playing,
    }

    // 全局热键 Ctrl+Alt+O：随时直接调出/隐藏信箱窗口（配合伴随模式与启动器包装）。
    private const int HotKeyId = 0x4F51;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint VirtualKeyO = 0x4F;
    private HwndSource? _hookSource;

    [DllImport("user32.dll")]
    private static extern int RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern int UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hookSource = (HwndSource)PresentationSource.FromVisual(this);
        _hookSource?.AddHook(WndProc);
        RegisterHotKey(_hookSource?.Handle ?? IntPtr.Zero, HotKeyId, ModControl | ModAlt, VirtualKeyO);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotKeyId)
        {
            ToggleMailboxVisibility();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public MainWindow()
    {
        InitializeComponent();
        var letterTypeface = ReplyLetterRenderer.CreateLetterTypeface(FontWeights.Normal);
        ReplyTextBlock.FontFamily = letterTypeface.FontFamily;
        ReplyTextBlock.FontSize = ReplyLetterRenderer.BodyFontSize;
        ReplyTextBlock.LineHeight = ReplyLetterRenderer.BodyLineHeight;
        LetterDateText.FontFamily = letterTypeface.FontFamily;
        LetterDateText.FontSize = ReplyLetterRenderer.DateFontSize;
        PaperBackground.Source = ReplyLetterRenderer.LetterPaperSource;
        LinkInstalledIndexTts();
        RefreshTtsActions();
        AddRenameMenu(HelloItem);
        AddRenameMenu(WelcomeItem);
        RefreshCharacter();

        _autoLetterTimer.Tick += AutoLetterTimer_OnTick;
        _autoLetterTimer.Start();
        Loaded += async (_, _) => await TryGenerateAutoLetterAsync();
        Closed += (_, _) =>
        {
            _autoLetterTimer.Stop();
            _readAloudCts?.Cancel();
            _speechPlayer?.Close();
            if (_hookSource is not null)
            {
                UnregisterHotKey(_hookSource.Handle, HotKeyId);
            }
        };
        if (Environment.GetCommandLineArgs().Any(arg => string.Equals(arg, "--watch", StringComparison.OrdinalIgnoreCase)))
        {
            Visibility = Visibility.Hidden;
            ShowInTaskbar = false;
            _gameWatchTimer.Tick += GameWatchTimer_OnTick;
            _gameWatchTimer.Start();
        }
    }

    private void RefreshCharacter()
    {
        var character = CharacterStore.Current;
        _characterId = character.Id;
        UserStyleStore.MigrateLegacyEntries(_characterId);
        CharacterButton.Content = $"角色：{character.Name} ▾";
        CharacterButton.ToolTip = $"当前角色：{character.Name}；点击切换角色与独立记忆";
        ComposeTitleText.Text = $"给{character.Name}写信";
        HelloItem.Visibility = WelcomeItem.Visibility = character.Id == CharacterStore.DefaultId ? Visibility.Visible : Visibility.Collapsed;
        ReloadSavedLetters();
        if (_savedLetters.Count > 0)
        {
            var newest = _savedLetters[0];
            ShowLetter(newest.Subject, FormatDate(newest.CreatedAt), newest.Draft, newest.Reply, newest.Id.ToString("N"));
            SelectItem(_savedLetterItems[0]);
        }
        else if (character.Id == CharacterStore.DefaultId)
        {
            ShowLetter("你好", "2026-08-26 18:08", "你好", _helloReply, LetterTitleStore.HelloKey);
            SelectItem(HelloItem);
        }

        else
        {
            ShowLetter("新的独立信箱", FormatDate(DateTime.Now), "还没有寄出的信。", $"这是{character.Name}的独立信箱。\n\n还没有往来记录，也没有其他角色的记忆。写下第一封信吧。");
        }
    }

    private void GameWatchTimer_OnTick(object? sender, EventArgs e)
    {
        // 伴随模式：游戏一开，信箱自动弹出；游戏关闭不强行收起，
        // 窗口可用 Ctrl+Alt+O 直接调出/隐藏。
        var game = Process.GetProcessesByName("Olivia").FirstOrDefault(process => process.MainWindowHandle != IntPtr.Zero);
        if (game is null)
        {
            _userHiddenByHotkey = false;
            return;
        }

        if (Visibility == Visibility.Visible || _userHiddenByHotkey)
        {
            return;
        }

        ShowInTaskbar = true;
        Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ToggleMailboxVisibility()
    {
        if (IsVisible)
        {
            _userHiddenByHotkey = true;
            Hide();
        }
        else
        {
            _userHiddenByHotkey = false;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }
    }

    private async void AutoLetterTimer_OnTick(object? sender, EventArgs e) => await TryGenerateAutoLetterAsync();

    private async Task TryGenerateAutoLetterAsync()
    {
        if (_isSettingsOpen || _isGeneratingAutoLetter || !MimoClient.IsConfigured)
        {
            return;
        }

        var characterId = _characterId;
        var character = CharacterStore.Get(characterId);
        var settings = AutoLetterStore.Load(characterId);
        var now = DateTime.Now;
        var history = LetterStore.Load(characterId);
        var scheduledDue = settings.IntervalMinutes >= 10
            && (settings.LastSentAt is null || now - settings.LastSentAt.Value >= TimeSpan.FromMinutes(settings.IntervalMinutes));
        var aiMinimumInterval = TimeSpan.FromMinutes(settings.AiInitiatedMinimumIntervalMinutes);
        var aiCanCheck = settings.AiInitiatedEnabled
            && settings.AiInitiatedMinimumIntervalMinutes >= 0
            && (settings.AiInitiatedMinimumIntervalMinutes == 0
                || ((settings.LastSentAt is null || now - settings.LastSentAt.Value >= aiMinimumInterval)
                    && (settings.LastAiInitiatedDecisionAt is null || now - settings.LastAiInitiatedDecisionAt.Value >= aiMinimumInterval)));
        if (!scheduledDue && !aiCanCheck)
        {
            return;
        }

        try
        {
            _isGeneratingAutoLetter = true;
            if (scheduledDue)
            {
                await GenerateAutoLetterAsync(history, characterId, character, settings, isAiInitiated: false);
                return;
            }

            settings.LastAiInitiatedDecisionAt = now;
            AutoLetterStore.Save(settings, characterId);
            if (!await MimoClient.ShouldSendAiInitiatedLetterAsync(history, now, characterId))
            {
                return;
            }

            await GenerateAutoLetterAsync(history, characterId, character, settings, isAiInitiated: true);
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

    private async Task GenerateAutoLetterAsync(IReadOnlyList<SavedLetter> history, string characterId, CharacterProfile character,
        AutoLetterSettings settings, bool isAiInitiated)
    {
        var reply = await MimoClient.GenerateProactiveLetterAsync(history, characterId);
        var sentAt = DateTime.Now;
        var letter = new SavedLetter
        {
            CreatedAt = sentAt,
            Subject = $"{character.Name}的来信",
            Draft = isAiInitiated ? $"{character.Name}主动寄来的一封信" : $"{character.Name}定时寄来的一封信",
            Reply = reply,
            IsAutoLetter = true,
        };

        var storedLetters = LetterStore.Load(characterId);
        storedLetters.Insert(0, letter);
        LetterStore.Save(storedLetters, characterId);
        settings.LastSentAt = sentAt;
        AutoLetterStore.Save(settings, characterId);
        if (CharacterStore.Current.Id != characterId)
        {
            return;
        }

        ReloadSavedLetters();
        ShowLetter(letter.Subject, FormatDate(letter.CreatedAt), letter.Draft, letter.Reply, letter.Id.ToString("N"));
        SelectItem(_savedLetterItems[0]);
        TryAutoReadAloud(letter.Id.ToString("N"), letter.Reply);
    }

    internal void ImportReferenceLetters(IReadOnlyList<PersonaReferenceLetter> references, string characterId)
    {
        var storedLetters = LetterStore.Load(characterId);
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

        LetterStore.Save(storedLetters, characterId);
        if (_characterId != characterId)
        {
            return;
        }

        ReloadSavedLetters();
        var newest = _savedLetters[0];
        ShowLetter(newest.Subject, FormatDate(newest.CreatedAt), newest.Draft, newest.Reply, newest.Id.ToString("N"));
        SelectItem(_savedLetterItems[0]);
    }

    private void ReloadSavedLetters()
    {
        _letterTitles = LetterTitleStore.Load(_characterId);
        HelloTitleText.Text = _letterTitles.GetValueOrDefault(LetterTitleStore.HelloKey, "你好");
        WelcomeTitleText.Text = _letterTitles.GetValueOrDefault(LetterTitleStore.WelcomeKey, "欢迎来到信箱");
        _savedLetters.Clear();
        _savedLetters.AddRange(LetterStore.Load(_characterId).OrderByDescending(letter => letter.CreatedAt));
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
        var compose = new ComposeWindow(_savedLetters.ToList(), _characterId) { Owner = this };
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
                var storedLetters = LetterStore.Load(args.CharacterId);
                storedLetters.Insert(0, letter);
                LetterStore.Save(storedLetters, args.CharacterId);
            }
            catch (IOException)
            {
                MessageBox.Show(this, "这封信暂时无法写入本机信箱。请确认磁盘可写后再试。", "未能保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _ = MimoClient.LearnUserStyleAsync(letter.Draft, args.CharacterId);

            if (_characterId != args.CharacterId)
            {
                return;
            }

            ReloadSavedLetters();
            ShowLetter(letter.Subject, FormatDate(letter.CreatedAt), letter.Draft, letter.Reply, letter.Id.ToString("N"));
            SelectItem(_savedLetterItems[0]);
            TryAutoReadAloud(letter.Id.ToString("N"), letter.Reply);
        };
        compose.ShowDialog();
    }

    private void ApiSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isSettingsOpen = true;
        try
        {
            var settings = new ApiSettingsWindow { Owner = this };
            settings.ShowDialog();
        }
        finally
        {
            _isSettingsOpen = false;
            RefreshTtsActions();
        }
    }

    private void LinkInstalledIndexTts()
    {
        var preferences = TtsPreferencesStore.Load();
        var detectedRoot = IndexTtsClient.FindInstalledRoot(preferences.IndexTtsRoot);
        if (detectedRoot is null || string.Equals(detectedRoot, preferences.IndexTtsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        preferences.IndexTtsRoot = detectedRoot;
        TtsPreferencesStore.Save(preferences);
        DiagnosticLog.Write("tts", $"auto_link root={DiagnosticLog.Redact(detectedRoot)}");
    }

    private void RefreshTtsActions()
    {
        var preferences = TtsPreferencesStore.Load();
        var visible = preferences.Enabled && IndexTtsClient.IsReady(preferences)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReadAloudButton.Visibility = visible;
        RegenerateAudioButton.Visibility = visible;
    }

    private void CharacterButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isSettingsOpen = true;
        try
        {
            var characters = new CharacterWindow { Owner = this };
            if (characters.ShowDialog() == true)
            {
                RefreshCharacter();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "无法切换角色", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isSettingsOpen = false;
        }
    }

    private void MailboxItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button item)
        {
            return;
        }

        SelectItem(item);
        if (item == WelcomeItem)
        {
            ShowLetter("欢迎来到信箱", "2026-08-26 17:32", "欢迎来到信箱", "你好。\n\n这里会收下你想慢慢写的事。信不需要写得漂亮，也不用急着讲完；有些话像最后几个音，先欠着也没有关系。\n\n窗外有一点风，我刚把练习本合上。想说的话，可以写给我。\n\n—— 林离", LetterTitleStore.WelcomeKey);
        }
        else
        {
            ShowLetter("你好", "2026-08-26 18:08", "你好", _helloReply, LetterTitleStore.HelloKey);
        }
    }

    private void ShowLetter(string subject, string date, string sentText, string reply, string? recordKey = null)
    {
        CancelReadAloud();
        _currentLetterKey = recordKey;
        LetterTitleEditor.Visibility = Visibility.Collapsed;
        LetterTitleButton.Visibility = Visibility.Visible;
        LetterTitleButton.IsEnabled = recordKey is not null;
        if (recordKey is not null)
        {
            subject = _letterTitles.GetValueOrDefault(recordKey, subject);
        }

        SubjectText.Text = subject;
        LetterTitleButton.ToolTip = $"{subject}（点击重命名）";
        DateText.Text = date;
        _currentSubject = subject;
        _currentDate = date;
        _currentDraft = sentText;
        _currentReply = reply;
        _currentReplyPages = ReplyLetterRenderer.RenderPages(reply, new Size(554, 310));
        _currentSentImage = SentLetterRenderer.Render(sentText, new Size(554, 310), date[..10]);
        UpdateLetterDisplay();
        ReplyScroll.ScrollToTop();
        SentImage.Source = _currentSentImage;
        LetterScrollViewer.ScrollToTop();
    }

    // 三层分离：纸面与边框固定不动，文字层独立滚动，日期钉在纸角。
    private void UpdateLetterDisplay()
    {
        ReplyTextBlock.Text = _currentReply;
        LetterDateText.Text = _currentDate.Length >= 10 ? _currentDate[..10] : _currentDate;
        ReplyScroll.ScrollToTop();
    }

    private void FullLetterButton_OnClick(object sender, RoutedEventArgs e)
    {
        new FullLetterWindow(_currentReply, _currentDate) { Owner = this }.Show();
    }

    private void RegenerateAudioButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_speechPhase != SpeechPhase.Idle || string.IsNullOrWhiteSpace(_currentReply))
        {
            return;
        }

        _ = StartReadAloudAsync(_characterId, _currentLetterKey, _currentReply, interactive: true, regenerate: true);
    }

    private async void ReadAloudButton_OnClick(object sender, RoutedEventArgs e)
    {
        switch (_speechPhase)
        {
            case SpeechPhase.Generating:
                _readAloudCts?.Cancel();
                return;
            case SpeechPhase.Playing:
                StopSpeechPlayback();
                return;
        }

        if (string.IsNullOrWhiteSpace(_currentReply))
        {
            return;
        }

        await StartReadAloudAsync(_characterId, _currentLetterKey, _currentReply, interactive: true);
    }

    private void TryAutoReadAloud(string? recordKey, string reply)
    {
        var preferences = TtsPreferencesStore.Load();
        if (!preferences.Enabled || !preferences.AutoReadNewLetters)
        {
            return;
        }

        _ = StartReadAloudAsync(_characterId, recordKey, reply, interactive: false);
    }

    private async Task StartReadAloudAsync(string characterId, string? recordKey, string reply, bool interactive, bool regenerate = false)
    {
        if (_speechPhase != SpeechPhase.Idle)
        {
            return;
        }

        var preferences = TtsPreferencesStore.Load();
        if (!preferences.Enabled)
        {
            if (interactive)
            {
                MessageBox.Show(this, "信件朗读还没启用。请在「AI 模型设置」里勾选启用后再试。", "未启用朗读", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        await RunReadAloudAsync(characterId, recordKey, reply, interactive, forceCpu: false, regenerate);
    }

    private async Task RunReadAloudAsync(string characterId, string? recordKey, string reply, bool interactive, bool forceCpu, bool regenerate = false)
    {
        if (_speechPhase != SpeechPhase.Idle)
        {
            return;
        }

        _speechPhase = SpeechPhase.Generating;
        _activeSpeechLabel = regenerate ? RegenerateAudioButtonLabel : ReadAloudButtonLabel;
        _activeSpeechLabel.Text = forceCpu ? "CPU 生成中" : "生成中";
        var speechKey = recordKey;
        var speechReply = reply;
        var cts = new CancellationTokenSource();
        _readAloudCts = cts;
        var progress = new Progress<TtsProgress>(item =>
        {
            if (_speechPhase == SpeechPhase.Generating)
            {
                _activeSpeechLabel.Text = item.Message;
            }
        });
        try
        {
            var wavPath = await _ttsClient.GenerateAsync(
                characterId, speechKey, speechReply, progress, cts.Token, forceCpu,
                regenerate ? Random.Shared.Next(1, int.MaxValue) : null, regenerate);
            if (_speechPhase != SpeechPhase.Generating || cts.IsCancellationRequested)
            {
                EndSpeechPhase();
                return;
            }

            if (_currentLetterKey != speechKey || _currentReply != speechReply)
            {
                // 用户已经切到别的信，不自动播放旧的音频。
                EndSpeechPhase();
                return;
            }

            _speechPhase = SpeechPhase.Playing;
            _activeSpeechLabel.Text = "停止朗读";
            var player = new System.Windows.Media.MediaPlayer();
            _speechPlayer = player;
            player.MediaEnded += (_, _) => StopSpeechPlayback();
            player.Open(new Uri(wavPath));
            player.Play();
        }
        catch (VramInsufficientException exception) when (interactive && !forceCpu)
        {
            EndSpeechPhase();
            var retryWithCpu = MessageBox.Show(
                this,
                exception.Message + "\n\n要改用 CPU 慢速生成吗？（不需要显存，整封约 5–10 分钟）",
                "显存不足",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (retryWithCpu == MessageBoxResult.Yes)
            {
                await RunReadAloudAsync(characterId, speechKey, speechReply, interactive, forceCpu: true);
            }
        }
        catch (OperationCanceledException)
        {
            EndSpeechPhase();
        }
        catch (Exception exception)
        {
            EndSpeechPhase();
            if (interactive)
            {
                MessageBox.Show(this, exception.Message, "无法朗读", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                DiagnosticLog.Write("tts", $"auto_read_failed error={DiagnosticLog.Redact(exception.Message)}");
            }
        }
        finally
        {
            if (_readAloudCts == cts)
            {
                _readAloudCts = null;
                cts.Dispose();
            }
        }
    }

    private void StopSpeechPlayback()
    {
        if (_speechPlayer is not null)
        {
            _speechPlayer.Stop();
            _speechPlayer.Close();
            _speechPlayer = null;
        }

        EndSpeechPhase();
    }

    private void CancelReadAloud()
    {
        if (_speechPhase == SpeechPhase.Generating)
        {
            _readAloudCts?.Cancel();
            _speechPhase = SpeechPhase.Idle;
            ReadAloudButtonLabel.Text = "朗读";
        }
        else if (_speechPhase == SpeechPhase.Playing)
        {
            StopSpeechPlayback();
        }
    }

    private void EndSpeechPhase()
    {
        _speechPhase = SpeechPhase.Idle;
        ReadAloudButtonLabel.Text = "朗读";
        RegenerateAudioButtonLabel.Text = "重新生成";
        _activeSpeechLabel = null;
    }

    private async void DownloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentReplyPages.Count == 0 || _currentSentImage is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            AddExtension = false,
            FileName = $"{LetterExport.SafeFileName(_currentSubject)}-{_currentDate[..10]}",
            Filter = "PNG 图像（输入文件名前缀）|*.png",
            Title = "选择信件图片的保存前缀（包含全部回信页）",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        LetterExport.SavePair(_currentSentImage, _currentReplyPages, dialog.FileName, CharacterStore.Get(_characterId).Name);
        await FlashLabelAsync(DownloadButtonLabel, $"已存{_currentReplyPages.Count + 1}张", "下载");
    }

    private async void ShareButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentReplyPages.Count == 0 || _currentSentImage is null)
        {
            return;
        }

        var data = new DataObject();
        data.SetImage(LetterExport.Combine(_currentReplyPages, _currentSentImage));
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
            Tag = letter.Id.ToString("N"),
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
            Text = _letterTitles.GetValueOrDefault(letter.Id.ToString("N"), letter.Subject),
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
            ShowLetter(letter.Subject, FormatDate(letter.CreatedAt), letter.Draft, letter.Reply, letter.Id.ToString("N"));
            SelectItem(item);
        };
        AddRenameMenu(item);

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

    private void UpdateMailboxTitle() => MailboxTitleText.Text = $"我的信箱  {_savedLetters.Count + (_characterId == CharacterStore.DefaultId ? BuiltInLetterCount : 0)}";

    private void AddRenameMenu(Button item)
    {
        item.ToolTip = "右键可重命名这条聊天记录";
        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) =>
        {
            item.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.BeginInvoke(new Action(BeginLetterTitleEdit));
        };
        item.ContextMenu = new ContextMenu { Items = { rename } };
    }

    private void LetterTitleButton_OnClick(object sender, RoutedEventArgs e) => BeginLetterTitleEdit();

    private void BeginLetterTitleEdit()
    {
        if (_currentLetterKey is null)
        {
            return;
        }

        LetterTitleBox.Text = _currentSubject;
        LetterTitleButton.Visibility = Visibility.Collapsed;
        LetterTitleEditor.Visibility = Visibility.Visible;
        LetterTitleBox.Focus();
        LetterTitleBox.SelectAll();
    }

    private void SaveLetterTitleButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentLetterKey is null)
        {
            return;
        }

        try
        {
            var title = LetterTitleStore.Save(_characterId, _currentLetterKey, LetterTitleBox.Text);
            ReloadSavedLetters();
            var selected = _currentLetterKey switch
            {
                LetterTitleStore.HelloKey => HelloItem,
                LetterTitleStore.WelcomeKey => WelcomeItem,
                _ => _savedLetterItems.FirstOrDefault(item => item.Tag as string == _currentLetterKey),
            };
            if (selected is not null)
            {
                SelectItem(selected);
            }

            SubjectText.Text = _currentSubject = title;
            LetterTitleButton.ToolTip = $"{title}（点击重命名）";
            EndLetterTitleEdit();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "无法重命名聊天记录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelLetterTitleButton_OnClick(object sender, RoutedEventArgs e) => EndLetterTitleEdit();

    private void EndLetterTitleEdit()
    {
        LetterTitleEditor.Visibility = Visibility.Collapsed;
        LetterTitleButton.Visibility = Visibility.Visible;
        LetterTitleButton.Focus();
    }

    private static string FormatDate(DateTime date) => date.ToString("yyyy-MM-dd HH:mm");

    private static string FirstLine(string value)
    {
        var text = value.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "一封信";
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
        if (LetterTitleEditor.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                EndLetterTitleEdit();
            }
            else if (e.Key == Key.Enter && LetterTitleBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                SaveLetterTitleButton_OnClick(sender, e);
            }

            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}















