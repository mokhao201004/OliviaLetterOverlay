using System.Runtime.InteropServices;

namespace OliviaLetterOverlay;

public sealed record LocalModelOption(string Model, string Title, string DownloadSize, string RecommendedFor, int MinimumRamGb);

internal static class LocalModelCatalog
{
    public static IReadOnlyList<LocalModelOption> Models { get; } =
    [
        new("qwen3:0.6b", "轻量 · Qwen3 0.6B", "约 523 MB", "8 GB 内存或仅想先试用", 8),
        new("qwen3:1.7b", "轻量增强 · Qwen3 1.7B", "约 1.4 GB", "8–12 GB 内存", 8),
        new("qwen3:4b", "推荐 · Qwen3 4B", "约 2.5 GB", "16 GB 内存，书信聊天更自然", 16),
        new("qwen3:8b", "高质量 · Qwen3 8B", "约 5.2 GB", "24 GB 以上内存，或有独显", 24),
        new("qwen3:14b", "高质量 · Qwen3 14B", "约 9.3 GB", "32 GB 以上内存或较大显存", 32),
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
