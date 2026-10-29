using System.Reflection;
using System.Runtime.InteropServices;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 加载嵌入资源的原生 Win32 图标（HICON）。
// 彻底脱离 System.Drawing.Common，具备真正的纯原生 AOT 零依赖特性。
internal static unsafe class AppIcons
{
    private static readonly Lazy<IntPtr> LazyAppIcon = new(() => LoadIconFromResource("app.ico"));
    private static readonly Lazy<IntPtr> LazyTrayIcon = new(() => LoadIconFromResource("tray.ico"));
    private static readonly Lazy<IntPtr> LazyIconConn = new(() => LoadIconFromResource("icon_conn.ico"));
    private static readonly Lazy<IntPtr> LazyIconSec = new(() => LoadIconFromResource("icon_sec.ico"));
    private static readonly Lazy<IntPtr> LazyIconOpt = new(() => LoadIconFromResource("icon_opt.ico"));

    public static IntPtr AppHandle => LazyAppIcon.Value;
    public static IntPtr TrayHandle => LazyTrayIcon.Value;
    public static IntPtr EvasionTrayHandle => LazyTrayIcon.Value;

    public static IntPtr IconConnHandle => LazyIconConn.Value;
    public static IntPtr IconSecHandle => LazyIconSec.Value;
    public static IntPtr IconOptHandle => LazyIconOpt.Value;

    private static IntPtr LoadIconFromResource(string fileName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
            {
                return IntPtr.Zero;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return IntPtr.Zero;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length < 22)
            {
                return IntPtr.Zero;
            }

            // 读取 ICO 头：bytes[4..6] 为图标数量
            var count = BitConverter.ToUInt16(bytes, 4);
            if (count == 0)
            {
                return IntPtr.Zero;
            }

            // 选择尺寸适中或最大的图标条目
            var bestIndex = 0;
            var maxArea = -1;
            for (var i = 0; i < count; i++)
            {
                var offset = 6 + i * 16;
                var w = bytes[offset] == 0 ? 256 : (int)bytes[offset];
                var h = bytes[offset + 1] == 0 ? 256 : (int)bytes[offset + 1];
                var area = w * h;
                if (area > maxArea)
                {
                    maxArea = area;
                    bestIndex = i;
                }
            }

            var entryOffset = 6 + bestIndex * 16;
            var bytesInRes = BitConverter.ToUInt32(bytes, entryOffset + 8);
            var imageOffset = BitConverter.ToUInt32(bytes, entryOffset + 12);

            if (imageOffset + bytesInRes > bytes.Length)
            {
                return IntPtr.Zero;
            }

            fixed (byte* pBits = &bytes[imageOffset])
            {
                return NativeMethods.CreateIconFromResourceEx(
                    pBits,
                    bytesInRes,
                    true,
                    0x00030000,
                    0,
                    0,
                    0);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
