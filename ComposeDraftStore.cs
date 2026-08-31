using System.IO;
using System.Text;
using System.Text.Json;

namespace OliviaLetterOverlay;

internal sealed class ComposeDraft
{
    public string Text { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

internal static class ComposeDraftStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Load(string? characterId = null)
    {
        var file = GetStorageFile(characterId);
        if (!File.Exists(file))
        {
            return string.Empty;
        }

        try
        {
            var draft = JsonSerializer.Deserialize<ComposeDraft>(File.ReadAllText(file), JsonOptions);
            return draft?.Text ?? string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public static void Save(string text, string? characterId = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Clear(characterId);
            return;
        }

        var directory = CharacterStore.GetDataDirectory(characterId);
        Directory.CreateDirectory(directory);
        var file = GetStorageFile(characterId);
        var temporary = file + ".tmp";
        var draft = new ComposeDraft { Text = text, UpdatedAt = DateTime.Now };
        File.WriteAllText(temporary, JsonSerializer.Serialize(draft, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, file, true);
    }

    public static void Clear(string? characterId = null)
    {
        var file = GetStorageFile(characterId);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private static string GetStorageFile(string? characterId) =>
        Path.Combine(CharacterStore.GetDataDirectory(characterId), "compose-draft.json");
}
