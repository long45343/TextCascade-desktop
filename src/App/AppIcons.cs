using System.Drawing;
using System.Reflection;

namespace TextCascadeSharp.App;

// 加载嵌入资源的图标。app.ico 作为主窗口图标，tray.ico 作为系统托盘图标。
// csproj 中通过 <EmbeddedResource Include="assets\app.ico" /> 嵌入资源。
internal static class AppIcons
{
    // 主窗口图标。加载失败时回退到系统默认 Application 图标
    public static Icon App { get; } = LoadIcon("app.ico") ?? SystemIcons.Application;

    // 托盘图标。加载失败时回退到主窗口图标
    public static Icon Tray { get; } = LoadIcon("tray.ico") ?? App;

    // 规避状态下的托盘图标（灰度指示）。加载失败回退到系统警示图标
    public static Icon EvasionTray { get; } = CreateGrayscaleIcon(Tray) ?? SystemIcons.Warning;

    // 从程序集嵌入资源中按文件名加载图标
    private static Icon? LoadIcon(string fileName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            // 资源名格式为 <默认命名空间>.<文件夹>.<文件名>，这里用后缀匹配
            var resourceName = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            // 加载失败回退到系统图标，不抛异常
            return null;
        }
    }

    private static Icon? CreateGrayscaleIcon(Icon source)
    {
        try
        {
            using var bmp = source.ToBitmap();
            for (var y = 0; y < bmp.Height; y++)
            {
                for (var x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.A > 0)
                    {
                        var gray = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                        bmp.SetPixel(x, y, Color.FromArgb(c.A, gray, gray, gray));
                    }
                }
            }
            var hIcon = bmp.GetHicon();
            var icon = Icon.FromHandle(hIcon);
            return (Icon)icon.Clone();
        }
        catch
        {
            return null;
        }
    }
}
