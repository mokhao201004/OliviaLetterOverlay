using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class PersonaProfile
{
    public DateTime UpdatedAt { get; set; }

    public int SourceImageCount { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public List<PersonaReferenceLetter> ReferenceLetters { get; set; } = [];

    public List<string> Memories { get; set; } = [];
}

public sealed class PersonaReferenceLetter
{
    public string Subject { get; set; } = string.Empty;

    public string Draft { get; set; } = string.Empty;

    public string Reply { get; set; } = string.Empty;
}

internal sealed class PersonaAnalysisResult
{
    public string Prompt { get; init; } = string.Empty;

    public List<PersonaReferenceLetter> Letters { get; init; } = [];

    public List<string> Memories { get; init; } = [];
}

internal static class PersonaStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static PersonaProfile? Load(string? characterId = null)
    {
        var storageFile = Path.Combine(CharacterStore.GetDataDirectory(characterId), "persona-profile.json");
        try
        {
            if (!File.Exists(storageFile))
            {
                return null;
            }

            return JsonSerializer.Deserialize<PersonaProfile>(File.ReadAllText(storageFile), JsonOptions);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(PersonaProfile profile, string? characterId = null)
    {
        var directory = CharacterStore.GetDataDirectory(characterId);
        Directory.CreateDirectory(directory);
        var storageFile = Path.Combine(directory, "persona-profile.json");
        var temporaryFile = storageFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(profile, JsonOptions));
        File.Move(temporaryFile, storageFile, true);
    }
}
