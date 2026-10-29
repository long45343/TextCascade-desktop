using System.Reflection;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 负责在进程启动时将内置的裁剪版 MiSans 字体（包含 Normal 与真 Demibold 半粗体）加载到 GDI 内存字体库。
// 引入原生 Demibold 矢量轮廓，彻底杜绝 GDI 算法重影涂抹导致的标题模糊。
internal static unsafe class EmbeddedFonts
{
    private static IntPtr _fontHandleRegular;
    private static IntPtr _fontHandleDemibold;
    private static bool _initialized;

    public static string FontFamilyRegular { get; private set; } = "Microsoft YaHei UI";
    public static string FontFamilyDemibold { get; private set; } = "Microsoft YaHei UI";

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        var assembly = Assembly.GetExecutingAssembly();
        _fontHandleRegular = LoadFontFromStream(assembly, "misans.ttf", "MiSans Normal", out var nameReg);
        if (!string.IsNullOrEmpty(nameReg))
        {
            FontFamilyRegular = nameReg;
        }

        _fontHandleDemibold = LoadFontFromStream(assembly, "misans-demibold.ttf", "MiSans Demibold", out var nameDemi);
        if (!string.IsNullOrEmpty(nameDemi))
        {
            FontFamilyDemibold = nameDemi;
        }
    }

    private static IntPtr LoadFontFromStream(Assembly assembly, string resourceFileName, string expectedFontName, out string actualFontName)
    {
        actualFontName = string.Empty;
        try
        {
            var resourceName = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceFileName, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                return IntPtr.Zero;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return IntPtr.Zero;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0)
            {
                return IntPtr.Zero;
            }

            fixed (byte* pBytes = bytes)
            {
                var handle = NativeMethods.AddFontMemResourceEx(pBytes, (uint)bytes.Length, IntPtr.Zero, out var count);
                if (handle != IntPtr.Zero && count > 0)
                {
                    actualFontName = expectedFontName;
                    return handle;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to load embedded font: {resourceFileName}", ex);
        }
        return IntPtr.Zero;
    }

    public static void Dispose()
    {
        if (_fontHandleRegular != IntPtr.Zero)
        {
            NativeMethods.RemoveFontMemResourceEx(_fontHandleRegular);
            _fontHandleRegular = IntPtr.Zero;
        }
        if (_fontHandleDemibold != IntPtr.Zero)
        {
            NativeMethods.RemoveFontMemResourceEx(_fontHandleDemibold);
            _fontHandleDemibold = IntPtr.Zero;
        }
    }
}
