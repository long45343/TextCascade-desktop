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

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplicationContext(launchedFromStartup));
    }
}
