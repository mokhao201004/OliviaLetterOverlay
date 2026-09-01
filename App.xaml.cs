using System.Configuration;
using System.Data;
using System.Runtime.InteropServices;
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
        ThreadCpuDiagnostics.RegisterCurrentThread("Olivia.UI");
        GpuPipelineDiagnostics.RegisterThread("Olivia.UI", "startup");
        ThreadCpuDiagnostics.LogConfigurationOnce();
        GpuPipelineDiagnostics.StartWatchdog();
        try
        {
            var dpiContextApplied = SetProcessDpiAwarenessContext(PerMonitorV2Context);
            DiagnosticLog.Write("dpi", $"ProcessDpiAwareness=PerMonitorV2 applied={dpiContextApplied}");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("dpi", $"ProcessDpiAwareness=PerMonitorV2 failed={exception.Message}");
        }

        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticLog.Write("app.error", $"dispatcher_exception type={args.Exception.GetType().FullName} message={args.Exception.Message} stack={args.Exception.StackTrace}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                DiagnosticLog.Write("app.error", $"unhandled_exception type={exception.GetType().FullName} message={exception.Message} stack={exception.StackTrace}");
            }
        };
        // 单实例：开机自启与启动器包装可能同时尝试拉起，只允许一个存活。
        _singleInstanceMutex = new Mutex(true, "OliviaLetterOverlay.SingleInstance", out var isNewInstance);
        if (!isNewInstance)
        {
            DiagnosticLog.Write("app", "second instance blocked by single-instance mutex");
            Shutdown();
            return;
        }

        DiagnosticLog.Write("app", $"started version={typeof(App).Assembly.GetName().Version}");
        try
        {
            var desktopDetector = new DesktopHostDetector();
            desktopDetector.Log(desktopDetector.Capture());
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("desktop.detector", $"capture_failed type={exception.GetType().Name} message={exception.Message}");
        }
        base.OnStartup(e);
    }

    private static readonly IntPtr PerMonitorV2Context = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
