using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

internal static class UserStyleStore
{
    public const string Marker = "用户说话：";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<string> Load(string? characterId = null)
    {
        var storageFile = GetStorageFile(characterId);
        try
        {
            if (!File.Exists(storageFile))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(storageFile), JsonOptions) ?? [];
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

    public static void Save(IReadOnlyList<string> observations, string? characterId = null)
    {
        var directory = CharacterStore.GetDataDirectory(characterId);
        Directory.CreateDirectory(directory);
        var storageFile = GetStorageFile(characterId);
        var temporaryFile = storageFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(observations, JsonOptions));
        File.Move(temporaryFile, storageFile, overwrite: true);
    }

    public static List<string> Add(string observation, int limit, string? characterId = null)
    {
        var merged = Merge(Load(characterId), observation, limit);
        Save(merged, characterId);
        return merged;
    }

    public static int Clear(string? characterId = null)
    {
        var count = Load(characterId).Count;
        Save([], characterId);
        return count;
    }

    public static void MigrateLegacyEntries(string? characterId = null)
    {
        var profile = PersonaStore.Load(characterId);
        if (profile is null)
        {
            return;
        }

        var legacyStyles = profile.Memories.Where(item => item.StartsWith(Marker, StringComparison.Ordinal)).ToList();
        if (legacyStyles.Count == 0)
        {
            return;
        }

        profile.Memories = profile.Memories.Where(item => !item.StartsWith(Marker, StringComparison.Ordinal)).ToList();
        PersonaStore.Save(profile, characterId);

        var observations = legacyStyles.Concat(Load(characterId)).Distinct(StringComparer.Ordinal).ToList();
        var limit = StylePreferencesStore.Load().StyleMemoryLimit;
        Save(limit > 0 ? observations.Take(limit).ToList() : observations, characterId);
    }

    public static List<string> Merge(IReadOnlyList<string> existing, string observation, int limit)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(observation))
        {
            result.Add(observation);
        }

        result.AddRange(existing.Where(item => !item.Equals(observation, StringComparison.Ordinal)));
        result = result.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToList();
        return limit > 0 ? result.Take(limit).ToList() : result;
    }

    private static string GetStorageFile(string? characterId) => Path.Combine(
        CharacterStore.GetDataDirectory(characterId),
        "user-style-observations.json");
}
