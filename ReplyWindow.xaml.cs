using System.Globalization;
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
        ReplyImage.Source = ReplyLetterRenderer.Render(reply, new Size(620, 380));
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
    private static readonly BitmapImage Paper = new(new Uri("pack://application:,,,/Assets/letter-paper.png", UriKind.Absolute));

    public static BitmapSource Render(string reply, Size size)
    {
        var width = (int)size.Width;
        var height = (int)size.Height;
        var visual = new DrawingVisual();

        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(Paper, new Rect(0, 0, width, height));
            DrawFittedReply(drawing, reply, width * .047, height * .092, width * .906, height * .74);
            DrawDate(drawing, DateTime.Today.ToString("yyyy-MM-dd"), width, height);
        }

        return ToBitmap(visual, width, height);
    }

    internal static void DrawText(DrawingContext drawing, string text, double x, double y, double maxWidth, double maxHeight, double fontSize, double lineHeight, FontWeight weight)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("KaiTi"), FontStyles.Normal, weight, FontStretches.Normal),
            fontSize,
            new SolidColorBrush(Color.FromRgb(46, 41, 34)),
            1.0)
        {
            MaxTextWidth = maxWidth,
            MaxTextHeight = maxHeight,
            LineHeight = lineHeight,
            Trimming = TextTrimming.WordEllipsis,
        };
        drawing.DrawText(formatted, new Point(x, y));
    }

    private static void DrawFittedReply(DrawingContext drawing, string text, double x, double y, double maxWidth, double maxHeight)
    {
        for (var fontSize = 16.2; fontSize >= 12; fontSize -= .4)
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.GetCultureInfo("zh-CN"),
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("KaiTi"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                fontSize,
                new SolidColorBrush(Color.FromRgb(46, 41, 34)),
                1.0)
            {
                MaxTextWidth = maxWidth,
                LineHeight = fontSize * 1.35,
            };

            if (formatted.Height <= maxHeight)
            {
                drawing.DrawText(formatted, new Point(x, y));
                return;
            }
        }

        DrawText(drawing, text, x, y, maxWidth, maxHeight, 12, 16.2, FontWeights.Normal);
    }

    internal static void DrawDate(DrawingContext drawing, string date, int width, int height)
    {
        var formatted = new FormattedText(
            date,
            CultureInfo.GetCultureInfo("zh-CN"),
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("KaiTi"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            13.5,
            new SolidColorBrush(Color.FromRgb(46, 41, 34)),
            1.0);
        drawing.DrawText(formatted, new Point(width - formatted.Width - width * .055, height - formatted.Height - height * .035));
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

            ReplyLetterRenderer.DrawText(drawing, title, width * .046, height * .09, width * .56, height * .2, 17.2, 21, FontWeights.Normal);
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
