using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

internal static class LetterTitleStore
{
    public const string HelloKey = "builtin-hello";
    public const string WelcomeKey = "builtin-welcome";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Dictionary<string, string> Load(string characterId)
    {
        var path = Path.Combine(CharacterStore.GetDataDirectory(characterId), "letter-titles.json");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("Invalid letter titles.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("历史记录名称文件无法读取，未覆盖已有名称。请保留 letter-titles.json。", exception);
        }
    }

    public static string Save(string characterId, string recordKey, string title)
    {
        title = title.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 40 || title.Any(char.IsControl))
        {
            throw new InvalidOperationException("聊天记录名称请填写 1–40 字，不要包含换行。");
        }

        var isBuiltIn = recordKey is HelloKey or WelcomeKey;
        if ((!isBuiltIn && !Guid.TryParseExact(recordKey, "N", out _))
            || (isBuiltIn && characterId != CharacterStore.DefaultId))
        {
            throw new InvalidOperationException("找不到要重命名的聊天记录。");
        }

        var titles = Load(characterId);
        titles[recordKey] = title;
        var directory = CharacterStore.GetDataDirectory(characterId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "letter-titles.json");
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(titles, JsonOptions));
        File.Move(temporaryPath, path, true);
        return title;
    }
}
