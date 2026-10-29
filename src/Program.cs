using System.Threading;
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
                NativeMethods.MessageBox(
                    IntPtr.Zero,
                    UiText.AlreadyRunning,
                    "TextCascade",
                    NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
            }
            return;
        }

        // 初始化 PerMonitorV2 高 DPI 感知与内置 MiSans 内存字体
        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        EmbeddedFonts.Initialize();

        // 进程级未捕获异常兜底记录日志
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.LogError($"Unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.LogError("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        // 启动主托盘宿主（纯原生 Win32）
        using var host = new TrayHost(launchedFromStartup);

        // 原生 Win32 消息循环泵
        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(in msg);
            NativeMethods.DispatchMessage(in msg);
        }

        EmbeddedFonts.Dispose();
    }
}
