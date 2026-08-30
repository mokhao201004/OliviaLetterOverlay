using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OliviaLetterOverlay;

public partial class ReplyWindow : Window
{
    public ReplyWindow(string reply)
    {
        InitializeComponent();
        ReplyPages.ItemsSource = ReplyLetterRenderer.RenderPages(reply, new Size(600, 380));
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}

internal static class ReplyLetterRenderer
{
    private static readonly BitmapImage Paper = new(new Uri("pack://application:,,,/Assets/letter-paper-olivia-inspired-v2.png", UriKind.Absolute));
    private const string LetterFontFileName = "ChillZhuo.ttf";
    private static string? _resolvedFamilyName;

    internal static BitmapSource LetterPaperSource => Paper;

    public static IReadOnlyList<BitmapSource> RenderPages(string reply, Size size)
    {
        var width = (int)size.Width;
        var height = (int)size.Height;
        var pages = Paginate(reply, size);
        var images = new List<BitmapSource>();
        for (var index = 0; index < pages.Count; index++)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawImage(Paper, new Rect(0, 0, width, height));
                DrawThickenedText(drawing, FormatReply(pages[index], width), new Point(width * .042, height * .071));
                DrawDate(drawing, DateTime.Today.ToString("yyyy-MM-dd"), width, height);
                if (pages.Count > 1)
                {
                    DrawText(drawing, $"{index + 1} / {pages.Count}", width * .047, height * .91,
                        width * .3, height * .08, 12, 16.2, FontWeights.Normal);
                }
            }

            images.Add(ToBitmap(visual, width, height));
        }

        return images;
    }

    // 把分页位图垂直拼成一张完整长信：连续滚动的阅读基础。
    // 每页边缘 1px 边框重叠 2 像素，避免拼接处出现双线。
    public static BitmapSource RenderFullLetter(IReadOnlyList<BitmapSource> pages)
    {
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("没有可拼接的信页。");
        }

        var width = pages[0].PixelWidth;
        var overlap = 2;
        var totalHeight = pages.Sum(page => page.PixelHeight) - overlap * (pages.Count - 1);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var y = 0d;
            foreach (var page in pages)
            {
                drawing.DrawImage(page, new Rect(0, y, width, page.PixelHeight));
                y += page.PixelHeight - overlap;
            }
        }

        var bitmap = new RenderTargetBitmap(width, totalHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    internal static IReadOnlyList<string> Paginate(string reply, Size size)
    {
        if (reply.Length == 0) return [string.Empty];
        // Measure the actual font layout, and never split a surrogate pair or combining character.
        var boundaries = StringInfo.ParseCombiningCharacters(reply).Append(reply.Length).ToArray();
        var pages = new List<string>();
        var start = 0;
        while (start < boundaries.Length - 1)
        {
            var low = start + 1;
            var high = boundaries.Length - 1;
            var end = low;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var text = reply[boundaries[start]..boundaries[middle]];
                if (FormatReply(text, size.Width).Height <= size.Height * .74)
                {
                    end = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            pages.Add(reply[boundaries[start]..boundaries[end]]);
            start = end;
        }

        return pages;
    }

    internal static Typeface CreateLetterTypeface(FontWeight weight)
    {
        // 寒蝉手拙体与寄出的信纸一致；字体文件缺失时才回退系统楷体。
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", LetterFontFileName);
        var family = File.Exists(fontPath)
            ? new FontFamily(fontPath + "#" + ResolveFamilyName(fontPath))
            : new FontFamily("KaiTi");
        return new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
    }

    private static string ResolveFamilyName(string fontPath)
    {
        _resolvedFamilyName ??= TryReadFamilyName(fontPath) ?? "KaiTi";
        return _resolvedFamilyName;
    }

    private static string? TryReadFamilyName(string fontPath)
    {
        try
        {
            var glyph = new GlyphTypeface(new Uri(fontPath));
            return glyph.FamilyNames.Values.FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UriFormatException or NotSupportedException)
        {
            return null;
        }
    }

    internal static FormattedText FormatReply(string reply, double width) =>
        new(
            reply.Trim('\r', '\n'),
            CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight,
            CreateLetterTypeface(FontWeights.Normal),
            22,
            new SolidColorBrush(Color.FromRgb(30, 26, 22)),
            1.0)
        {
            MaxTextWidth = width * .916,
            LineHeight = 28,
            Trimming = TextTrimming.None,
        };

    // 单次绘制：寒蝉手拙体在正文字号下笔画已经足够黑实，叠绘反而让笔画密的字发胖。
    private static void DrawThickenedText(DrawingContext drawing, FormattedText formatted, Point point)
    {
        drawing.DrawText(formatted, point);
    }

    internal static void DrawText(DrawingContext drawing, string text, double x, double y, double maxWidth, double maxHeight, double fontSize, double lineHeight, FontWeight weight)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight,
            CreateLetterTypeface(weight),
            fontSize,
            new SolidColorBrush(Color.FromRgb(46, 41, 34)),
            1.0)
        {
            MaxTextWidth = maxWidth,
            MaxTextHeight = maxHeight,
            LineHeight = lineHeight,
            Trimming = TextTrimming.WordEllipsis,
        };
        DrawThickenedText(drawing, formatted, new Point(x, y));
    }

    internal static void DrawDate(DrawingContext drawing, string date, int width, int height)
    {
        var formatted = new FormattedText(
            date,
            CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight,
            CreateLetterTypeface(FontWeights.Normal),
            16,
            new SolidColorBrush(Color.FromRgb(46, 41, 34)),
            1.0);
        DrawThickenedText(drawing, formatted, new Point(width - formatted.Width - width * .04, height - formatted.Height - height * .04));
    }

    internal static BitmapSource ToBitmap(DrawingVisual visual, int width, int height)
    {
        const int renderScale = 2;
        var bitmap = new RenderTargetBitmap(width * renderScale, height * renderScale, 96 * renderScale, 96 * renderScale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

internal static class SentLetterRenderer
{
    private static readonly BitmapImage Paper = new(new Uri("pack://application:,,,/Assets/sent-paper.png", UriKind.Absolute));
    private static readonly BitmapImage PortraitStamp = new(new Uri("pack://application:,,,/Assets/portrait-stamp.png", UriKind.Absolute));

    public static BitmapSource Render(string text, Size size, string date)
    {
        var width = (int)size.Width;
        var height = (int)size.Height;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(Paper, new Rect(0, 0, width, height));

            // Mask only the generated placeholder stamp, preserving the paper, waves, and air-mail stripe.
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(244, 240, 232)), null, new Rect(width * .838, height * .025, width * .154, height * .302));
            drawing.DrawImage(PortraitStamp, new Rect(width * .838, height * .025, width * .148, height * .302));

            var title = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Replace("\r", string.Empty).Split('\n')[0].Trim();
            if (title.Length > 26)
            {
                title = title[..26] + "…";
            }

            ReplyLetterRenderer.DrawText(drawing, title, width * .046, height * .09, width * .56, height * .2, 22, 28, FontWeights.Normal);
            ReplyLetterRenderer.DrawDate(drawing, date, width, height);
        }
        return ReplyLetterRenderer.ToBitmap(visual, width, height);
    }
}

internal static class ComposerPaperRenderer
{
    private static readonly BitmapImage Paper = new(new Uri("pack://application:,,,/Assets/composer-paper.png", UriKind.Absolute));
    private static readonly BitmapImage PortraitStamp = new(new Uri("pack://application:,,,/Assets/portrait-stamp.png", UriKind.Absolute));

    public static BitmapSource Render(Size size)
    {
        var width = (int)size.Width;
        var height = (int)size.Height;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(Paper, new Rect(0, 0, width, height));

            // Replace only the old generated stamp; preserve the stationary texture and postmark waves.
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(248, 246, 242)), null, new Rect(width * .84, height * .045, width * .145, height * .265));
            drawing.DrawImage(PortraitStamp, new Rect(width * .842, height * .045, width * .137, height * .265));
        }
        return ReplyLetterRenderer.ToBitmap(visual, width, height);
    }
}
