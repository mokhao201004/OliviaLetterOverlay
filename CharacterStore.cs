using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public sealed class CharacterProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

internal sealed class CharacterCatalog
{
    public string ActiveId { get; set; } = CharacterStore.DefaultId;
    public List<CharacterProfile> Characters { get; set; } = [new() { Id = CharacterStore.DefaultId, Name = "林离" }];
}

internal static class CharacterStore
{
    public const string DefaultId = "default";
    private static readonly string RootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OliviaLetterOverlay");
    private static readonly string CatalogPath = Path.Combine(RootDirectory, "characters.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static CharacterProfile Current
    {
        get
        {
            var catalog = Load();
            return catalog.Characters.Single(character => character.Id == catalog.ActiveId);
        }
    }

    public static IReadOnlyList<CharacterProfile> List() => Load().Characters;

    public static CharacterProfile Get(string id) => Load().Characters.FirstOrDefault(character => character.Id == id)
        ?? throw new InvalidOperationException("找不到这个角色，未读取其他角色的数据。");

    public static string GetDataDirectory(string? characterId = null)
    {
        var id = characterId ?? Current.Id;
        if (!IsValidId(id))
        {
            throw new InvalidOperationException("角色标识无效，未访问角色数据。");
        }

        // Existing installations keep their files in place and belong to the default character.
        return id == DefaultId ? RootDirectory : Path.Combine(RootDirectory, "characters", id);
    }

    public static CharacterProfile Create(string name, string prompt)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 40 || name.Any(char.IsControl))
        {
            throw new InvalidOperationException("请填写 1–40 字的角色名，不要包含换行。");
        }

        var catalog = Load();
        if (catalog.Characters.Any(character => string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("这个角色名已经存在，请换一个名字。");
        }

        var character = new CharacterProfile { Id = Guid.NewGuid().ToString("N"), Name = name };
        PersonaStore.Save(new PersonaProfile { UpdatedAt = DateTime.Now, Prompt = prompt.Trim() }, character.Id);
        catalog.Characters.Add(character);
        catalog.ActiveId = character.Id;
        Save(catalog);
        DiagnosticLog.Write("character", $"created and selected id={character.Id}");
        return character;
    }

    public static void Select(string id)
    {
        var catalog = Load();
        if (!catalog.Characters.Any(character => character.Id == id))
        {
            throw new InvalidOperationException("找不到要切换的角色。");
        }

        catalog.ActiveId = id;
        Save(catalog);
        DiagnosticLog.Write("character", $"selected id={id}");
    }

    private static CharacterCatalog Load()
    {
        if (!File.Exists(CatalogPath))
        {
            return new CharacterCatalog();
        }

        try
        {
            var catalog = JsonSerializer.Deserialize<CharacterCatalog>(File.ReadAllText(CatalogPath), JsonOptions);
            if (catalog is null || catalog.Characters is null
                || catalog.Characters.Any(character => character is null || !IsValidId(character.Id) || string.IsNullOrWhiteSpace(character.Name))
                || catalog.Characters.Select(character => character.Id).Distinct().Count() != catalog.Characters.Count
                || !catalog.Characters.Any(character => character.Id == DefaultId)
                || !catalog.Characters.Any(character => character.Id == catalog.ActiveId))
            {
                throw new JsonException("Invalid character catalog.");
            }

            return catalog;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("角色列表无法读取。请保留 characters.json 并导出日志，现有角色数据不会被重置。", exception);
        }
    }

    private static bool IsValidId(string id) => id == DefaultId || Guid.TryParseExact(id, "N", out _);

    private static void Save(CharacterCatalog catalog)
    {
        Directory.CreateDirectory(RootDirectory);
        var temporaryPath = CatalogPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(catalog, JsonOptions));
        File.Move(temporaryPath, CatalogPath, true);
    }
}
