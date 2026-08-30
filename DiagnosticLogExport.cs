using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace OliviaLetterOverlay;

internal static class DiagnosticLogExport
{
    public static async Task ShowAsync(Window owner)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出诊断日志",
            Filter = "诊断日志 (*.txt)|*.txt",
            FileName = $"Olivia-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExt = ".txt",
            AddExtension = true,
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        try
        {
            await Task.Run(() => DiagnosticLog.Export(dialog.FileName));
            MessageBox.Show(owner, "诊断日志已导出。API Key、网址参数和用户目录已脱敏，不包含信件和记忆。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, "无法写入日志文件，请选择一个可写目录后重试。", "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
