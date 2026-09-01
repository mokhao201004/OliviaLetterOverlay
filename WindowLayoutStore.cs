using System.IO;
using System.Text.Json;
using System.Windows;

namespace OliviaLetterOverlay;

internal sealed class WindowLayoutState
{
    public double Width { get; set; }
    public double Height { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public WindowState WindowState { get; set; }
}

internal static class WindowLayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string StorageFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OliviaLetterOverlay",
        "window-layout.json");

    public static WindowLayoutState? Load()
    {
        if (!File.Exists(StorageFile))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WindowLayoutState>(File.ReadAllText(StorageFile), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("window", $"layout_load_failed message={exception.Message}");
            return null;
        }
    }

    public static void Save(WindowLayoutState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(StorageFile)!;
            Directory.CreateDirectory(directory);
            var temporaryFile = StorageFile + ".tmp";
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temporaryFile, StorageFile, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("window", $"layout_save_failed message={exception.Message}");
        }
    }
}
