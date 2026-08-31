using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class MemoryPreferences
{
    // -1 means keep every memory, 0 means do not save memories, positive values are the maximum count.
    public int MemoryLimit { get; set; } = -1;
}

internal static class MemoryPreferencesStore
{
    private static readonly string StorageFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OliviaLetterOverlay",
        "memory-preferences.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static MemoryPreferences Load()
    {
        try
        {
            if (!File.Exists(StorageFile))
            {
                return new MemoryPreferences();
            }

            var preferences = JsonSerializer.Deserialize<MemoryPreferences>(File.ReadAllText(StorageFile), JsonOptions)
                ?? new MemoryPreferences();
            preferences.MemoryLimit = Math.Max(-1, preferences.MemoryLimit);
            return preferences;
        }
        catch (IOException)
        {
            return new MemoryPreferences();
        }
        catch (JsonException)
        {
            return new MemoryPreferences();
        }
    }

    public static void Save(MemoryPreferences preferences)
    {
        preferences.MemoryLimit = Math.Max(-1, preferences.MemoryLimit);
        var directory = Path.GetDirectoryName(StorageFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = StorageFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, JsonOptions));
        File.Move(temporary, StorageFile, overwrite: true);
    }

    public static List<string> ApplyLimit(IEnumerable<string> memories, int? limit = null)
    {
        var distinct = memories
            .Where(memory => !string.IsNullOrWhiteSpace(memory))
            .Select(memory => memory.Trim())
            .Reverse()
            .Distinct(StringComparer.Ordinal)
            .Reverse();
        var effectiveLimit = limit ?? Load().MemoryLimit;
        return effectiveLimit == 0
            ? []
            : effectiveLimit > 0
                ? distinct.TakeLast(effectiveLimit).ToList()
                : distinct.ToList();
    }
}
