using System.Threading;
using System.Windows.Forms;
using TextCascadeSharp.App;

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
        Application.Run(new TrayApplicationContext(launchedFromStartup));
    }
}
