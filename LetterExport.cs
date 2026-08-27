using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OliviaLetterOverlay;

internal static class LetterExport
{
    public static BitmapSource Combine(BitmapSource reply, BitmapSource sent)
    {
        const int width = 554;
        const int height = 629;
        const int renderScale = 2;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 26, 31)), null, new Rect(0, 0, width, height));
            drawing.DrawImage(reply, new Rect(0, 0, width, 310));
            drawing.DrawImage(sent, new Rect(0, 319, width, 310));
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

    public static void SavePair(BitmapSource sent, BitmapSource reply, string basePath)
    {
        var directory = Path.GetDirectoryName(basePath);
        var fileName = Path.GetFileNameWithoutExtension(basePath);
        var prefix = Path.Combine(string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory, SafeFileName(fileName));
        SavePng(sent, NextAvailablePath(prefix + "-我写的.png"));
        SavePng(reply, NextAvailablePath(prefix + "-林离回信.png"));
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
