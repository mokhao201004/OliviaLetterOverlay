using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

internal static class MusicTitleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string StorageFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OliviaLetterOverlay", "music-titles.json");

    public static Dictionary<string, string> Load()
    {
        if (!File.Exists(StorageFile))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorageFile), JsonOptions)
                ?? throw new JsonException("Invalid music titles.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("歌曲名称文件无法读取，未覆盖已有名称。请保留 music-titles.json。", exception);
        }
    }

    public static string Save(string folderName, string title)
    {
        title = title.Trim();
        if (string.IsNullOrEmpty(folderName) || string.IsNullOrEmpty(title) || title.Length > 60 || title.Any(char.IsControl))
        {
            throw new InvalidOperationException("歌曲名称请填写 1–60 字，不要包含换行。");
        }

        var titles = Load();
        titles[folderName] = title;
        var directory = Path.GetDirectoryName(StorageFile)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = StorageFile + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(titles, JsonOptions));
        File.Move(temporaryPath, StorageFile, true);
        return title;
    }
}
