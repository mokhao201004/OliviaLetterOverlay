using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OliviaLetterOverlay;

internal static class LetterExport
{
    public static BitmapSource Combine(IReadOnlyList<BitmapSource> replies, BitmapSource sent)
    {
        const int width = 554;
        var images = replies.Append(sent).ToList();
        var height = (int)Math.Ceiling(images.Sum(image => image.Height * width / image.Width) + (images.Count - 1) * 9);
        const int renderScale = 2;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 26, 31)), null, new Rect(0, 0, width, height));
            var top = 0.0;
            foreach (var image in images)
            {
                var imageHeight = image.Height * width / image.Width;
                drawing.DrawImage(image, new Rect(0, top, width, imageHeight));
                top += imageHeight + 9;
            }
        }

        var bitmap = new RenderTargetBitmap(width * renderScale, height * renderScale, 96 * renderScale, 96 * renderScale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static void SavePair(BitmapSource sent, IReadOnlyList<BitmapSource> replies, string basePath, string characterName)
    {
        var directory = Path.GetDirectoryName(basePath);
        var fileName = Path.GetFileNameWithoutExtension(basePath);
        var prefix = Path.Combine(string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory, SafeFileName(fileName));
        SavePng(sent, NextAvailablePath(prefix + "-我写的.png"));
        for (var index = 0; index < replies.Count; index++)
        {
            var pageSuffix = replies.Count > 1 ? $"-第{index + 1}页" : string.Empty;
            SavePng(replies[index], NextAvailablePath(prefix + $"-{SafeFileName(characterName)}回信{pageSuffix}.png"));
        }
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = string.Concat(name.Select(character => invalid.Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(result) ? "信件" : result;
    }

    private static string NextAvailablePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var index = 2;
        while (File.Exists(Path.Combine(directory, $"{fileName} ({index}){extension}")))
        {
            index++;
        }

        return Path.Combine(directory, $"{fileName} ({index}){extension}");
    }
}
