using System.Runtime.InteropServices;

namespace OliviaLetterOverlay;

public sealed record LocalModelOption(string Model, string Title, string DownloadSize, string RecommendedFor, int MinimumRamGb);

internal static class LocalModelCatalog
{
    public static IReadOnlyList<LocalModelOption> Models { get; } =
    [
        new("qwen3:0.6b", "轻量 · Qwen3 0.6B", "约 523 MB", "8 GB 内存或仅想先试用", 8),
        new("tinyllama", "轻量 · TinyLlama 1.1B", "约 638 MB", "8 GB 内存，响应速度快", 8),
        new("llama3.2:1b", "轻量 · Llama 3.2 1B", "约 1.3 GB", "8 GB 内存，日常问答", 8),
        new("gemma3:1b", "轻量 · Gemma 3 1B", "约 815 MB", "8 GB 内存，多语言短对话", 8),
        new("qwen3:1.7b", "轻量增强 · Qwen3 1.7B", "约 1.4 GB", "8–12 GB 内存", 8),
        new("llama3.2:3b", "均衡轻量 · Llama 3.2 3B", "约 2.0 GB", "8–12 GB 内存", 8),
        new("phi4-mini", "均衡轻量 · Phi-4 Mini 3.8B", "约 2.5 GB", "8–12 GB 内存", 8),
        new("deepseek-r1:1.5b", "推理轻量 · DeepSeek R1 1.5B", "约 1.1 GB", "8 GB 内存，轻量推理", 8),
        new("qwen3:4b", "推荐 · Qwen3 4B", "约 2.5 GB", "16 GB 内存，书信聊天更自然", 16),
        new("gemma3:4b", "均衡 · Gemma 3 4B", "约 3.3 GB", "16 GB 内存，多语言写作", 16),
        new("deepseek-r1:7b", "推理均衡 · DeepSeek R1 7B", "约 4.7 GB", "16 GB 内存，推理更强", 16),
        new("mistral:7b", "均衡 · Mistral 7B", "约 4.1 GB", "16 GB 内存，通用对话", 16),
        new("llama3.1:8b", "均衡 · Llama 3.1 8B", "约 4.9 GB", "16 GB 内存，通用写作", 16),
        new("qwen3:8b", "高质量 · Qwen3 8B", "约 5.2 GB", "24 GB 以上内存，或有独显", 24),
        new("deepseek-r1:8b", "推理高质量 · DeepSeek R1 8B", "约 4.9 GB", "24 GB 以上内存，推理更强", 24),
        new("qwen3:14b", "高质量 · Qwen3 14B", "约 9.3 GB", "32 GB 以上内存或较大显存", 32),
        new("gemma3:12b", "高质量 · Gemma 3 12B", "约 8.1 GB", "32 GB 以上内存，写作细腻", 32),
        new("phi4", "高质量 · Phi-4 14B", "约 9.1 GB", "32 GB 以上内存，推理与写作", 32),
        new("deepseek-r1:14b", "推理高质量 · DeepSeek R1 14B", "约 9.0 GB", "32 GB 以上内存", 32),
        new("gemma3:27b", "旗舰 · Gemma 3 27B", "约 17 GB", "32–48 GB 内存或大显存", 32),
        new("qwen3:32b", "旗舰 · Qwen3 32B", "约 20 GB", "48 GB 以上内存或大显存", 48),
        new("deepseek-r1:32b", "旗舰推理 · DeepSeek R1 32B", "约 20 GB", "48 GB 以上内存或大显存", 48),
        new("llama3.1:70b", "旗舰 · Llama 3.1 70B", "约 40 GB", "64 GB 以上内存或高端工作站", 64),
    ];

    public static string GetDeviceRecommendation()
    {
        var ram = GetInstalledMemoryGb();
        if (ram <= 0)
        {
            return "未能读取内存，建议先从 Qwen3 1.7B 或 4B 开始。";
        }

        var model = Models.LastOrDefault(item => item.MinimumRamGb <= ram) ?? Models[0];
        return $"检测到约 {ram} GB 内存，建议从“{model.Title}”开始。";
    }

    private static int GetInstalledMemoryGb()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? (int)Math.Round(status.TotalPhysical / 1024d / 1024d / 1024d)
            : 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
