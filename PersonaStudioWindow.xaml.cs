using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;

namespace OliviaLetterOverlay;

public partial class PersonaStudioWindow : Window
{
    private readonly List<string> _sentImagePaths = [];
    private readonly List<string> _replyImagePaths = [];
    private List<PersonaReferenceLetter> _referenceLetters = [];
    private List<string> _memories = [];
    private int _sourceImageCount;
    private string _analysisPrompt = string.Empty;

    public PersonaStudioWindow()
    {
        InitializeComponent();
        var activeProfile = PersonaStore.Load();
        if (activeProfile is null)
        {
            StatusText.Text = "尚未启用自定义人设。";
            return;
        }

        _analysisPrompt = activeProfile.Prompt;
        _referenceLetters = activeProfile.ReferenceLetters ?? [];
        _memories = activeProfile.Memories ?? [];
        _sourceImageCount = activeProfile.SourceImageCount;
        AnalysisBox.Text = BuildPreview();
        ApplyButton.IsEnabled = true;
        StatusText.Text = $"当前正使用已保存的人设（{activeProfile.UpdatedAt:yyyy-MM-dd HH:mm}）。";
    }

    private void AddSentImagesButton_OnClick(object sender, RoutedEventArgs e) => AddImages(_sentImagePaths, SentImagesList, "选择你发给她的信件截图");

    private void AddReplyImagesButton_OnClick(object sender, RoutedEventArgs e) => AddImages(_replyImagePaths, ReplyImagesList, "选择她回给你的信件截图");

    private void AddImages(List<string> target, System.Windows.Controls.ListBox listBox, string title)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "信件图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp",
            Title = title,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        AddImageFiles(target, listBox, dialog.FileNames);
    }

    private void ClearImagesButton_OnClick(object sender, RoutedEventArgs e)
    {
        _sentImagePaths.Clear();
        _replyImagePaths.Clear();
        SentImagesList.Items.Clear();
        ReplyImagesList.Items.Clear();
        AnalyzeButton.IsEnabled = false;
        StatusText.Text = "已清空待分析图片；已保存的人设不受影响。";
    }

    private async void AnalyzeButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AnalyzeButton.IsEnabled = false;
            ApplyButton.IsEnabled = false;
            StatusText.Text = "正在读取并分析图片…";
            var result = await MimoClient.AnalyzePersonaAsync(_sentImagePaths, _replyImagePaths);
            _analysisPrompt = result.Prompt;
            _referenceLetters = result.Letters;
            _memories = result.Memories;
            _sourceImageCount = _sentImagePaths.Count + _replyImagePaths.Count;
            if (_referenceLetters.Count != _sentImagePaths.Count)
            {
                StatusText.Text = $"只完整识别出 {_referenceLetters.Count} / {_sentImagePaths.Count} 组往来；请换清晰截图后重试，暂不写入侧边信箱。";
                return;
            }

            AnalysisBox.Text = BuildPreview();
            ApplyButton.IsEnabled = true;
            StatusText.Text = $"分析完成，识别出 {_referenceLetters.Count} 组往来。启用后会同步进侧边信箱。";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            AnalyzeButton.IsEnabled = _sentImagePaths.Count > 0 && _sentImagePaths.Count == _replyImagePaths.Count;
        }
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        var prompt = _analysisPrompt.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusText.Text = "没有可启用的人设内容。";
            return;
        }

        try
        {
            PersonaStore.Save(new PersonaProfile
            {
                UpdatedAt = DateTime.Now,
                SourceImageCount = _sourceImageCount,
                Prompt = prompt,
                ReferenceLetters = _referenceLetters,
                Memories = _memories,
            });
            if (Owner?.Owner is MainWindow mailbox)
            {
                mailbox.ImportReferenceLetters(_referenceLetters);
            }

            StatusText.Text = "已启用并保存。成对信件已加入侧边信箱，之后写信会自动使用。";
        }
        catch (System.IO.IOException)
        {
            StatusText.Text = "人设暂时无法写入本机，请确认磁盘可写后再试。";
        }
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void SentImagesList_OnDrop(object sender, DragEventArgs e) => AddDroppedImages(_sentImagePaths, SentImagesList, e);

    private void ReplyImagesList_OnDrop(object sender, DragEventArgs e) => AddDroppedImages(_replyImagePaths, ReplyImagesList, e);

    private void ImageList_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedImageFiles(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void AddDroppedImages(List<string> target, System.Windows.Controls.ListBox listBox, DragEventArgs e)
    {
        if (!TryGetDroppedImageFiles(e.Data, out var imageFiles))
        {
            StatusText.Text = "这里只能拖入 PNG、JPG、GIF、WebP 或 BMP 图片。";
            return;
        }

        AddImageFiles(target, listBox, imageFiles);
    }

    private void AddImageFiles(List<string> target, System.Windows.Controls.ListBox listBox, IEnumerable<string> files)
    {
        foreach (var file in files.Where(file => !target.Contains(file, StringComparer.OrdinalIgnoreCase)))
        {
            target.Add(file);
            listBox.Items.Add(System.IO.Path.GetFileName(file));
        }

        UpdateImageStatus();
    }

    private static bool TryGetDroppedImageFiles(IDataObject data, out string[] files)
    {
        files = [];
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] droppedFiles)
        {
            return false;
        }

        files = droppedFiles.Where(IsSupportedImage).ToArray();
        return files.Length > 0;
    }

    private static bool IsSupportedImage(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    private void UpdateImageStatus()
    {
        AnalyzeButton.IsEnabled = _sentImagePaths.Count > 0 && _sentImagePaths.Count == _replyImagePaths.Count;
        StatusText.Text = _sentImagePaths.Count == _replyImagePaths.Count
            ? $"已配对 {_sentImagePaths.Count} 组信件。"
            : $"我发的信 {_sentImagePaths.Count} 张，她回的信 {_replyImagePaths.Count} 张；请补齐后再分析。";
    }

    private string BuildPreview()
    {
        var preview = "【人设】\n" + _analysisPrompt;
        if (_memories.Count > 0)
        {
            preview += "\n\n【提炼记忆】\n" + string.Join("\n", _memories.Select((memory, index) => $"{index + 1}. {memory}"));
        }

        if (_referenceLetters.Count == 0)
        {
            return preview;
        }

        return preview + "\n\n【即将导入的成对记录】\n" + string.Join("\n\n", _referenceLetters.Select((letter, index) => $"第 {index + 1} 组\n我发：{letter.Draft}\n\n她回：{letter.Reply}"));
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
