using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class SavedLetter
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string Draft { get; set; } = string.Empty;

    public string Reply { get; set; } = string.Empty;

    public bool IsReference { get; set; }

    public bool IsAutoLetter { get; set; }
}

internal static class LetterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<SavedLetter> Load(string? characterId = null)
    {
        var storageFile = Path.Combine(CharacterStore.GetDataDirectory(characterId), "letters.json");
        try
        {
            if (!File.Exists(storageFile))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<SavedLetter>>(File.ReadAllText(storageFile), JsonOptions) ?? [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Save(IReadOnlyList<SavedLetter> letters, string? characterId = null)
    {
        var directory = CharacterStore.GetDataDirectory(characterId);
        Directory.CreateDirectory(directory);
        var storageFile = Path.Combine(directory, "letters.json");
        var temporaryFile = storageFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(letters, JsonOptions));
        File.Move(temporaryFile, storageFile, true);
    }
}
