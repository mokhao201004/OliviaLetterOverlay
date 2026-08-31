using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class ConversationDigest
{
    public string Summary { get; set; } = string.Empty;

    public int CompressedUserLetterCount { get; set; }
}

internal sealed class ConversationCompressionResult
{
    public string Summary { get; init; } = string.Empty;

    public List<string> Memories { get; init; } = [];
}

internal static class ConversationDigestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ConversationDigest Load(string? characterId = null)
    {
        var file = Path.Combine(CharacterStore.GetDataDirectory(characterId), "conversation-digest.json");
        try
        {
            if (!File.Exists(file))
            {
                return new ConversationDigest();
            }

            var digest = JsonSerializer.Deserialize<ConversationDigest>(File.ReadAllText(file), JsonOptions)
                ?? new ConversationDigest();
            digest.CompressedUserLetterCount = Math.Max(0, digest.CompressedUserLetterCount);
            return digest;
        }
        catch (IOException)
        {
            return new ConversationDigest();
        }
        catch (JsonException)
        {
            return new ConversationDigest();
        }
    }

    public static void Save(ConversationDigest digest, string? characterId = null)
    {
        var directory = CharacterStore.GetDataDirectory(characterId);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "conversation-digest.json");
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(digest, JsonOptions));
        File.Move(temporary, file, overwrite: true);
    }
}

internal static class ConversationCompressionService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public static void Queue(string characterId, IReadOnlyList<SavedLetter> history)
    {
        var preferences = ConversationPreferencesStore.Load();
        if (preferences.CompressionIntervalLetters <= 0 || !MimoClient.IsConfigured)
        {
            return;
        }

        var userCount = history.Count(IsUserLetter);
        var digest = ConversationDigestStore.Load(characterId);
        if (userCount - digest.CompressedUserLetterCount < preferences.CompressionIntervalLetters)
        {
            return;
        }

        var gate = Locks.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));
        _ = RunAsync(characterId, gate);
    }

    private static async Task RunAsync(string characterId, SemaphoreSlim gate)
    {
        if (!await gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            while (true)
            {
                var preferences = ConversationPreferencesStore.Load();
                if (preferences.CompressionIntervalLetters <= 0 || !MimoClient.IsConfigured)
                {
                    return;
                }

                var history = LetterStore.Load(characterId).Where(IsUserLetter).OrderBy(letter => letter.CreatedAt).ToList();
                var digest = ConversationDigestStore.Load(characterId);
                if (history.Count - digest.CompressedUserLetterCount < preferences.CompressionIntervalLetters)
                {
                    return;
                }

                var batch = history.Skip(digest.CompressedUserLetterCount).Take(preferences.CompressionIntervalLetters).ToList();
                if (batch.Count < preferences.CompressionIntervalLetters)
                {
                    return;
                }

                var result = await MimoClient.CompressConversationAsync(batch, digest.Summary, characterId);
                digest.Summary = result.Summary;
                digest.CompressedUserLetterCount += batch.Count;
                ConversationDigestStore.Save(digest, characterId);

                var profile = PersonaStore.Load(characterId) ?? new PersonaProfile();
                profile.Memories = MemoryPreferencesStore.ApplyLimit(profile.Memories.Concat(result.Memories));
                profile.UpdatedAt = DateTime.Now;
                PersonaStore.Save(profile, characterId);
                DiagnosticLog.Write("memory.compress", $"completed letters={batch.Count} total={digest.CompressedUserLetterCount} memories={result.Memories.Count}");
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("memory.compress", "failed error=" + DiagnosticLog.Redact(exception.Message));
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool IsUserLetter(SavedLetter letter) =>
        !letter.IsAutoLetter && !letter.IsReference && !string.IsNullOrWhiteSpace(letter.Draft) && !string.IsNullOrWhiteSpace(letter.Reply);
}
