using System.Reflection;
using System.Runtime.InteropServices;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 加载嵌入资源的原生 Win32 图标（HICON）。
// 彻底脱离 System.Drawing.Common，支持精准目标尺寸点对点加载，杜绝缩放模糊。
internal static unsafe class AppIcons
{
    private static readonly Lazy<IntPtr> LazyAppIcon = new(() => LoadIconFromResource("app.ico", 0));
    private static readonly Lazy<IntPtr> LazyAppIcon24 = new(() => LoadIconFromResource("app.ico", 24));
    private static readonly Lazy<IntPtr> LazyTrayIcon = new(() => LoadIconFromResource("tray.ico", 16));
    private static readonly Lazy<IntPtr> LazyIconConn = new(() => LoadIconFromResource("icon_conn.ico", 24));
    private static readonly Lazy<IntPtr> LazyIconSec = new(() => LoadIconFromResource("icon_sec.ico", 24));
    private static readonly Lazy<IntPtr> LazyIconOpt = new(() => LoadIconFromResource("icon_opt.ico", 24));

    public static IntPtr AppHandle => LazyAppIcon.Value;
    public static IntPtr AppHandle24 => LazyAppIcon24.Value;
    public static IntPtr TrayHandle => LazyTrayIcon.Value;
    public static IntPtr EvasionTrayHandle => LazyTrayIcon.Value;

    public static IntPtr IconConnHandle => LazyIconConn.Value;
    public static IntPtr IconSecHandle => LazyIconSec.Value;
    public static IntPtr IconOptHandle => LazyIconOpt.Value;

    private static IntPtr LoadIconFromResource(string fileName, int desiredSize)
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

            var count = BitConverter.ToUInt16(bytes, 4);
            if (count == 0)
            {
                return IntPtr.Zero;
            }

            // 精准匹配目标尺寸，避免运行期被 GDI 劣质最近邻插值缩放
            var bestIndex = 0;
            if (desiredSize > 0)
            {
                var minDiff = int.MaxValue;
                for (var i = 0; i < count; i++)
                {
                    var offset = 6 + i * 16;
                    var w = bytes[offset] == 0 ? 256 : (int)bytes[offset];
                    var diff = Math.Abs(w - desiredSize);
                    if (diff < minDiff)
                    {
                        minDiff = diff;
                        bestIndex = i;
                    }
                }
            }
            else
            {
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
