using System.IO;

namespace OliviaLetterOverlay;

public sealed record CachedSong(string FolderName, IReadOnlyList<string> Clips, DateTime LastSavedAt)
{
    // 支持 midi_编号_中文歌名；无论保留编号还是只留下中文目录名，都优先显示中文部分。
    public string? ChineseTitle
    {
        get
        {
            for (var index = 0; index < FolderName.Length; index++)
            {
                if (FolderName[index] is >= '一' and <= '龥')
                {
                    return FolderName[index..].Trim();
                }
            }

            return null;
        }
    }

    public string PickRandomClip(Random? random = null)
    {
        if (Clips.Count == 0)
        {
            throw new InvalidOperationException("这首本地歌曲没有可播放的视频片段。");
        }

        return Clips[(random ?? Random.Shared).Next(Clips.Count)];
    }
}

internal static class CachedMusicLibrary
{
    internal static string DefaultCacheRoot => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "miHoYo", "Olivia-steam", "cache", "studio", "video");

    internal static IReadOnlyList<CachedSong> Load(string? cacheRoot = null)
    {
        var configuredRoot = MusicPreferencesStore.Load().FolderPath;
        var root = cacheRoot ?? (string.IsNullOrWhiteSpace(configuredRoot) ? DefaultCacheRoot : configuredRoot);
        if (!Directory.Exists(root))
        {
            return [];
        }

        // 游戏原本用 midi_... 作为缓存目录名，但用户可以把目录改成中文歌名；
        // 因此只按“一级目录里是否有 MP4”识别，不依赖原始命名规则。
        return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .Select(folder =>
            {
                var clips = Directory.EnumerateFiles(folder, "*.mp4", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var lastSavedAt = clips.Length == 0
                    ? Directory.GetLastWriteTime(folder)
                    : clips.Select(File.GetLastWriteTime).Max();
                return new CachedSong(Path.GetFileName(folder), clips, lastSavedAt);
            })
            .Where(song => song.Clips.Count > 0)
            .OrderByDescending(song => song.LastSavedAt)
            .ToArray();
    }
}
