using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class AutoLetterSettings
{
    public int IntervalMinutes { get; set; }

    public DateTime? LastSentAt { get; set; }

    public bool AiInitiatedEnabled { get; set; }

    public int AiInitiatedMinimumIntervalMinutes { get; set; } = 180;

    public DateTime? LastAiInitiatedDecisionAt { get; set; }
}

internal static class AutoLetterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AutoLetterSettings Load(string? characterId = null)
    {
        var storageFile = Path.Combine(CharacterStore.GetDataDirectory(characterId), "auto-letter-settings.json");
        try
        {
            if (!File.Exists(storageFile))
            {
                return new AutoLetterSettings();
            }

            return JsonSerializer.Deserialize<AutoLetterSettings>(File.ReadAllText(storageFile), JsonOptions) ?? new AutoLetterSettings();
        }
        catch (IOException)
        {
            return new AutoLetterSettings();
        }
        catch (JsonException)
        {
            return new AutoLetterSettings();
        }
    }

    public static void Save(AutoLetterSettings settings, string? characterId = null)
    {
        var directory = CharacterStore.GetDataDirectory(characterId);
        Directory.CreateDirectory(directory);
        var storageFile = Path.Combine(directory, "auto-letter-settings.json");
        var temporaryFile = storageFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryFile, storageFile, true);
    }
}
