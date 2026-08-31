using System.Windows;
using System.Windows.Input;

namespace OliviaLetterOverlay;

public partial class MemoryWindow : Window
{
    private readonly string _characterId = CharacterStore.Current.Id;

    public MemoryWindow()
    {
        InitializeComponent();
        UserStyleStore.MigrateLegacyEntries(_characterId);
        MemoryTitleText.Text = $"{CharacterStore.Get(_characterId).Name} · 记忆库";
        var profile = PersonaStore.Load(_characterId);
        var memories = profile?.Memories ?? [];
        MemoryBox.Text = string.Join(Environment.NewLine, memories);
        StatusText.Text = FormatStatus(memories.Count);
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var memories = MemoryPreferencesStore.ApplyLimit(MemoryBox.Text
            .Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(memory => memory.Trim()));

        try
        {
            var profile = PersonaStore.Load(_characterId) ?? new PersonaProfile();
            profile.UpdatedAt = DateTime.Now;
            profile.Memories = memories;
            PersonaStore.Save(profile, _characterId);
            StatusText.Text = FormatStatus(memories.Count, saved: true);
        }
        catch (System.IO.IOException)
        {
            StatusText.Text = "记忆暂时无法写入本机，请确认磁盘可写后再试。";
        }
    }

    private async void AnalyzeMemoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AnalyzeMemoryButton.IsEnabled = false;
            StatusText.Text = "正在分析侧边信箱中的往来…";
            var analyzedMemories = await MimoClient.AnalyzeMemoriesAsync(LetterStore.Load(_characterId));
            var currentMemories = MemoryBox.Text
                .Replace("\r", string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(memory => memory.Trim())
                .Where(memory => memory.Length > 0)
                .ToList();

            foreach (var memory in analyzedMemories)
            {
                if (!currentMemories.Contains(memory, StringComparer.Ordinal))
                {
                    currentMemories.Add(memory);
                }
            }

            MemoryBox.Text = string.Join(Environment.NewLine, currentMemories);
            StatusText.Text = $"已提炼 {analyzedMemories.Count} 条，已写入编辑区；点击“保存记忆”后按设置保存。";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            AnalyzeMemoryButton.IsEnabled = true;
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

    private static string FormatStatus(int count, bool saved = false)
    {
        var limit = MemoryPreferencesStore.Load().MemoryLimit;
        var description = limit switch
        {
            0 => "不保存",
            -1 => "不限量",
            _ => $"最多 {limit} 条",
        };
        return saved ? $"已保存 {count} 条（{description}）。" : count == 0 ? $"还没有保存的记忆（{description}）。" : $"当前有 {count} 条记忆（{description}）。";
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
