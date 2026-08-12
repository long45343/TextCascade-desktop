using System.Threading;
using System.Windows.Forms;
using TextCascadeSharp.App;
using TextCascadeSharp.Core;

namespace TextCascadeSharp;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var launchedFromStartup = args.Any(static arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(initiallyOwned: true, name: @"Local\TextCascade", out var createdNew);
        if (!createdNew)
        {
            if (!launchedFromStartup)
            {
                MessageBox.Show(UiText.AlreadyRunning, "TextCascade", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        // ApplicationConfiguration.Initialize() 由源生成器展开为
        //   Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)
        //   Application.EnableVisualStyles()
        //   Application.SetCompatibleTextRenderingDefault(false)
        // 配置项来自 csproj 的 <ApplicationHighDpiMode> 等属性。
        // PerMonitorV2 比 SystemAware 更适合多显示器 + 不同缩放下运行。
        ApplicationConfiguration.Initialize();
        // 进程级兜底：UI 线程/后台任务异常只记日志，不让托盘常驻程序直接崩掉。
        // SetUnhandledExceptionMode 必须在创建任何窗口前调用。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Logger.LogError("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.LogError($"Unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.LogError("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        Application.Run(new TrayApplicationContext(launchedFromStartup));
    }
}
