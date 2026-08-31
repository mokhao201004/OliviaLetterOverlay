using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class MusicPreferences
{
    public string FolderPath { get; set; } = string.Empty;
}

internal static class MusicPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string StorageFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OliviaLetterOverlay", "music-preferences.json");

    public static MusicPreferences Load()
    {
        if (!File.Exists(StorageFile))
        {
            return new MusicPreferences();
        }

        try
        {
            return JsonSerializer.Deserialize<MusicPreferences>(File.ReadAllText(StorageFile), JsonOptions)
                ?? throw new JsonException("Invalid music preferences.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("本地音乐文件夹设置无法读取，未覆盖原有设置。请保留 music-preferences.json。", exception);
        }
    }

    public static void Save(string? folderPath)
    {
        var path = folderPath?.Trim() ?? string.Empty;
        if (path.Length > 0 && !Directory.Exists(path))
        {
            throw new InvalidOperationException("本地音乐文件夹不存在，请重新选择，或清空后使用游戏缓存目录。");
        }

        var directory = Path.GetDirectoryName(StorageFile)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = StorageFile + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new MusicPreferences { FolderPath = path }, JsonOptions));
        File.Move(temporaryPath, StorageFile, true);
    }
}
