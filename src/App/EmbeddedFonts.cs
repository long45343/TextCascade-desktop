using System.Reflection;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 负责在进程启动时将内置的裁剪版 MiSans 字体加载到 GDI 内存字体库。
// 免去往 Windows 系统字体目录写入文件的过程，零权限要求，零系统污染。
internal static unsafe class EmbeddedFonts
{
    private static IntPtr _fontHandle;
    private static bool _initialized;

    public static string FontFamily { get; private set; } = "Microsoft YaHei UI";

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("misans.ttf", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                return;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0)
            {
                return;
            }

            fixed (byte* pBytes = bytes)
            {
                _fontHandle = NativeMethods.AddFontMemResourceEx(pBytes, (uint)bytes.Length, IntPtr.Zero, out var count);
                if (_fontHandle != IntPtr.Zero && count > 0)
                {
                    // MiSans Normal 成功载入当前进程私有 GDI 字体表
                    FontFamily = "MiSans Normal";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Failed to load embedded MiSans font", ex);
        }
    }

    public static void Dispose()
    {
        if (_fontHandle != IntPtr.Zero)
        {
            NativeMethods.RemoveFontMemResourceEx(_fontHandle);
            _fontHandle = IntPtr.Zero;
        }
    }
}
