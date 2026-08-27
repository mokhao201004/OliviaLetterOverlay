using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class SavedLetter
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime CreatedAt { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string Draft { get; set; } = string.Empty;

    public string Reply { get; set; } = string.Empty;

    public bool IsReference { get; set; }

    public bool IsAutoLetter { get; set; }
}

internal static class LetterStore
{
    private static readonly string StorageDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OliviaLetterOverlay");

    private static readonly string StorageFile = Path.Combine(StorageDirectory, "letters.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<SavedLetter> Load()
    {
        try
        {
            if (!File.Exists(StorageFile))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<SavedLetter>>(File.ReadAllText(StorageFile), JsonOptions) ?? [];
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

    public static void Save(IReadOnlyList<SavedLetter> letters)
    {
        Directory.CreateDirectory(StorageDirectory);
        var temporaryFile = StorageFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(letters, JsonOptions));
        File.Move(temporaryFile, StorageFile, true);
    }
}
