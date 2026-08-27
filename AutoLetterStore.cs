using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class AutoLetterSettings
{
    public int IntervalMinutes { get; set; }

    public DateTime? LastSentAt { get; set; }
}

internal static class AutoLetterStore
{
    private static readonly string StorageDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OliviaLetterOverlay");

    private static readonly string StorageFile = Path.Combine(StorageDirectory, "auto-letter-settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AutoLetterSettings Load()
    {
        try
        {
            if (!File.Exists(StorageFile))
            {
                return new AutoLetterSettings();
            }

            return JsonSerializer.Deserialize<AutoLetterSettings>(File.ReadAllText(StorageFile), JsonOptions) ?? new AutoLetterSettings();
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

    public static void Save(AutoLetterSettings settings)
    {
        Directory.CreateDirectory(StorageDirectory);
        var temporaryFile = StorageFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryFile, StorageFile, true);
    }
}
