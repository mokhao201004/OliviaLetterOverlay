using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class StylePreferences
{
    // 0 means keep every learned style observation forever.
    public int StyleMemoryLimit { get; set; } = 5;
}

internal static class StylePreferencesStore
{
    private static readonly string StorageFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OliviaLetterOverlay",
        "style-preferences.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static StylePreferences Load()
    {
        try
        {
            if (!File.Exists(StorageFile))
            {
                return new StylePreferences();
            }

            return JsonSerializer.Deserialize<StylePreferences>(File.ReadAllText(StorageFile), JsonOptions) ?? new StylePreferences();
        }
        catch (IOException)
        {
            return new StylePreferences();
        }
        catch (JsonException)
        {
            return new StylePreferences();
        }
    }

    public static void Save(StylePreferences preferences)
    {
        var directory = Path.GetDirectoryName(StorageFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = StorageFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, JsonOptions));
        File.Move(temporary, StorageFile, overwrite: true);
    }
}
