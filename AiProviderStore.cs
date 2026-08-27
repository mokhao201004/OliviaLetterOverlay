using System.IO;
using System.Text.Json;

namespace OliviaLetterOverlay;

public enum AiProviderKind
{
    Mimo,
    OpenAiCompatible,
    Ollama,
}

public sealed class AiProviderSettings
{
    public AiProviderKind Provider { get; set; } = AiProviderKind.Mimo;

    public string BaseUrl { get; set; } = string.Empty;

    public string Model { get; set; } = "mimo-v2.5";
}

internal static class AiProviderStore
{
    public const string DefaultOllamaBaseUrl = "http://127.0.0.1:11434";
    private const string CustomApiKeyVariable = "OLIVIA_COMPATIBLE_API_KEY";
    private static readonly string StorageDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OliviaLetterOverlay");
    private static readonly string StorageFile = Path.Combine(StorageDirectory, "ai-provider-settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AiProviderSettings Load()
    {
        try
        {
            if (!File.Exists(StorageFile))
            {
                return new AiProviderSettings();
            }

            return Normalize(JsonSerializer.Deserialize<AiProviderSettings>(File.ReadAllText(StorageFile), JsonOptions) ?? new AiProviderSettings());
        }
        catch (IOException)
        {
            return new AiProviderSettings();
        }
        catch (JsonException)
        {
            return new AiProviderSettings();
        }
    }

    public static void Save(AiProviderSettings settings)
    {
        var normalized = Normalize(settings);
        Directory.CreateDirectory(StorageDirectory);
        var temporaryFile = StorageFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporaryFile, StorageFile, true);
    }

    public static bool IsConfigured
    {
        get
        {
            var settings = Load();
            return settings.Provider switch
            {
                AiProviderKind.Mimo => !string.IsNullOrWhiteSpace(GetMimoApiKey()),
                AiProviderKind.OpenAiCompatible => IsHttpUrl(settings.BaseUrl) && !string.IsNullOrWhiteSpace(settings.Model) && !string.IsNullOrWhiteSpace(GetCompatibleApiKey()),
                AiProviderKind.Ollama => IsHttpUrl(settings.BaseUrl) && !string.IsNullOrWhiteSpace(settings.Model),
                _ => false,
            };
        }
    }

    public static string ProviderLabel(AiProviderKind provider) => provider switch
    {
        AiProviderKind.Mimo => "MiMo",
        AiProviderKind.OpenAiCompatible => "OpenAI 兼容接口",
        AiProviderKind.Ollama => "本地 Ollama",
        _ => "AI",
    };

    public static string MissingConfigurationMessage()
    {
        var settings = Load();
        return settings.Provider switch
        {
            AiProviderKind.Mimo => "请先在右上角头像里填写 MiMo API Key。内容仍在本地。",
            AiProviderKind.OpenAiCompatible => "请先补全 OpenAI 兼容接口的 URL、模型名和 API Key。内容仍在本地。",
            AiProviderKind.Ollama => "请先填写本地 Ollama 地址和模型名，并确认 Ollama 服务正在运行。内容仍在本地。",
            _ => "请先完成 AI 设置。内容仍在本地。",
        };
    }

    public static string? GetMimoApiKey() => GetEnvironmentValue("MIMO_API_KEY");

    public static string? GetCompatibleApiKey() => GetEnvironmentValue(CustomApiKeyVariable);

    public static void SaveMimoApiKey(string value) => SaveEnvironmentValue("MIMO_API_KEY", value);

    public static void SaveCompatibleApiKey(string value) => SaveEnvironmentValue(CustomApiKeyVariable, value);

    public static string NormalizeBaseUrl(string value) => value.Trim().TrimEnd('/');

    public static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static AiProviderSettings Normalize(AiProviderSettings settings)
    {
        settings.BaseUrl = NormalizeBaseUrl(settings.BaseUrl ?? string.Empty);
        settings.Model = (settings.Model ?? string.Empty).Trim();
        if (settings.Provider == AiProviderKind.Mimo)
        {
            settings.Model = "mimo-v2.5";
            settings.BaseUrl = string.Empty;
        }
        else if (settings.Provider == AiProviderKind.Ollama)
        {
            settings.BaseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl) ? DefaultOllamaBaseUrl : settings.BaseUrl;
            settings.Model = string.IsNullOrWhiteSpace(settings.Model) ? "qwen3:4b" : settings.Model;
        }

        return settings;
    }

    private static string? GetEnvironmentValue(string name)
    {
        var processValue = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(processValue))
        {
            return processValue.Trim();
        }

        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)?.Trim();
    }

    private static void SaveEnvironmentValue(string name, string value)
    {
        var trimmed = value.Trim();
        Environment.SetEnvironmentVariable(name, trimmed, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, trimmed, EnvironmentVariableTarget.Process);
    }
}
