using System.Configuration;
using System.Data;
using System.Threading;
using System.Windows;

namespace OliviaLetterOverlay;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：开机自启与启动器包装可能同时尝试拉起，只允许一个存活。
        _singleInstanceMutex = new Mutex(true, "OliviaLetterOverlay.SingleInstance", out var isNewInstance);
        if (!isNewInstance)
        {
            DiagnosticLog.Write("app", "second instance blocked by single-instance mutex");
            Shutdown();
            return;
        }

        DiagnosticLog.Write("app", $"started version={typeof(App).Assembly.GetName().Version}");
        base.OnStartup(e);
    }
}
