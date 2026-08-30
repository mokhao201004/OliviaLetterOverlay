using System.Text;

namespace OliviaLetterOverlay;

// 生僻字守卫：以 GB2312 一级常用字（3755 字，区位 16–55）为白名单判断是否常用；
// 已知的形近混淆对做自动纠正；其余生僻字记入诊断日志，便于持续补充映射。
internal static class RareCharGuard
{
    private static Encoding? _gb2312;
    private static bool _providerInitialized;

    public static bool IsCommon(char c)
    {
        if (c < 0x80)
        {
            return true;
        }

        if (c is '，' or '。' or '、' or '；' or '：' or '？' or '！' or '（' or '）' or '《' or '》' or '—' or '…' or '·')
        {
            return true;
        }

        var gb = GetGb2312();
        if (gb is null)
        {
            return true;
        }

        var bytes = gb.GetBytes(c.ToString());
        return bytes.Length == 2 && bytes[0] is >= 0xB0 and <= 0xD7;
    }

    public static string ReplaceKnownConfusions(string text) => text
        .Replace('椐', '据')
        .Replace('捃', '拾');

    private static Encoding? GetGb2312()
    {
        if (!_providerInitialized)
        {
            _providerInitialized = true;
            try
            {
                Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
            }

            try
            {
                _gb2312 = Encoding.GetEncoding("GB2312");
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
            }
        }

        return _gb2312;
    }
}
