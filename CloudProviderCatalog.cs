namespace OliviaLetterOverlay;

internal sealed record CloudProvider(string Id, string DisplayName, string BaseUrl);

internal static class CloudProviderCatalog
{
    public static IReadOnlyList<CloudProvider> Providers { get; } =
    [
        new("openai", "OpenAI", "https://api.openai.com/v1"),
        new("anthropic", "Anthropic", "https://api.anthropic.com/v1"),
        new("google", "Google Gemini（OpenAI 兼容）", "https://generativelanguage.googleapis.com/v1beta/openai"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1"),
        new("moonshot", "Moonshot Kimi", "https://api.moonshot.cn/v1"),
        new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4"),
        new("dashscope", "阿里云百炼 Qwen", "https://dashscope.aliyuncs.com/compatible-mode/v1"),
        new("volces", "火山方舟", "https://ark.cn-beijing.volces.com/api/v3"),
        new("xai", "xAI Grok", "https://api.x.ai/v1"),
        new("mistral", "Mistral AI", "https://api.mistral.ai/v1"),
        new("groq", "Groq", "https://api.groq.com/openai/v1"),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1"),
        new("siliconflow", "SiliconFlow 硅基流动", "https://api.siliconflow.cn/v1"),
        new("together", "Together AI", "https://api.together.xyz/v1"),
        new("custom", "OpenAI 兼容接口 / 中转站", string.Empty),
    ];

    public static CloudProvider? Find(string id) => Providers.FirstOrDefault(provider =>
        string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string DisplayName(string id) => Find(id)?.DisplayName ?? "OpenAI 兼容接口";
}
