using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class TtsPreferences
{
    public bool Enabled { get; set; }
    public string IndexTtsRoot { get; set; } = @"D:\codex work\IndexTTS-2.5";
    public bool AutoReadNewLetters { get; set; }
    public string ReferencePath { get; set; } = string.Empty;
    public int Seed { get; set; } = 20260830;
    public int IntervalSilenceMs { get; set; } = 200;
    public int MaxTextTokensPerSegment { get; set; } = 120;
    public double DurationFactor { get; set; } = 1.0;
}

internal static class TtsPreferencesStore
{
    private static readonly string StorageFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OliviaLetterOverlay",
        "tts-preferences.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static TtsPreferences Load()
    {
        try
        {
            if (!File.Exists(StorageFile))
            {
                return new TtsPreferences();
            }

            return JsonSerializer.Deserialize<TtsPreferences>(File.ReadAllText(StorageFile), JsonOptions) ?? new TtsPreferences();
        }
        catch (IOException)
        {
            return new TtsPreferences();
        }
        catch (JsonException)
        {
            return new TtsPreferences();
        }
    }

    public static void Save(TtsPreferences preferences)
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
