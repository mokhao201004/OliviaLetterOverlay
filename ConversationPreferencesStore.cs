using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class ConversationPreferences
{
    // -1 means all days, 0 means do not inject previous letters.
    public int ReplyHistoryDays { get; set; } = 7;

    // 0 disables automatic compression; positive values trigger every N user letters.
    public int CompressionIntervalLetters { get; set; } = 20;
}

internal static class ConversationPreferencesStore
{
    private static readonly string StorageFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OliviaLetterOverlay",
        "conversation-preferences.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ConversationPreferences Load()
    {
        try
        {
            if (!File.Exists(StorageFile))
            {
                return new ConversationPreferences();
            }

            var preferences = JsonSerializer.Deserialize<ConversationPreferences>(File.ReadAllText(StorageFile), JsonOptions)
                ?? new ConversationPreferences();
            preferences.ReplyHistoryDays = Math.Max(-1, preferences.ReplyHistoryDays);
            preferences.CompressionIntervalLetters = Math.Max(0, preferences.CompressionIntervalLetters);
            return preferences;
        }
        catch (IOException)
        {
            return new ConversationPreferences();
        }
        catch (JsonException)
        {
            return new ConversationPreferences();
        }
    }

    public static void Save(ConversationPreferences preferences)
    {
        preferences.ReplyHistoryDays = Math.Max(-1, preferences.ReplyHistoryDays);
        preferences.CompressionIntervalLetters = Math.Max(0, preferences.CompressionIntervalLetters);
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
