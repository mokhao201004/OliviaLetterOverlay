using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class OllamaPreferences
{
    public string InstallDirectory { get; set; } = string.Empty;
}

internal static class OllamaPreferencesStore
{
    private static readonly string StorageFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OliviaLetterOverlay",
        "ollama-preferences.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static OllamaPreferences Load()
    {
        try
        {
            if (!File.Exists(StorageFile))
            {
                return new OllamaPreferences();
            }

            return JsonSerializer.Deserialize<OllamaPreferences>(File.ReadAllText(StorageFile), JsonOptions) ?? new OllamaPreferences();
        }
        catch (IOException)
        {
            return new OllamaPreferences();
        }
        catch (JsonException)
        {
            return new OllamaPreferences();
        }
    }

    public static void Save(OllamaPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorageFile)!);
        var temporaryFile = StorageFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(preferences, JsonOptions));
        File.Move(temporaryFile, StorageFile, true);
    }
}
