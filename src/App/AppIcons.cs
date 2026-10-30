using System.Reflection;
using System.Runtime.InteropServices;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 加载嵌入资源的原生 Win32 图标（HICON）。
// 彻底脱离 System.Drawing.Common，支持精准目标尺寸点对点加载，杜绝缩放模糊。
internal static unsafe class AppIcons
{
    private static readonly Lazy<IntPtr> LazyAppIcon = new(() => LoadIconFromResource("app.ico", 0));
    private static readonly Lazy<IntPtr> LazyAppIcon16 = new(() => LoadIconFromResource("app.ico", 16));
    private static readonly Lazy<IntPtr> LazyTrayIcon = new(() => LoadIconFromResource("tray.ico", 16));

    // 卡片标题图标需按实际绘制尺寸加载：高 DPI 下固定用 24px 再被 GDI 放大就会出现锯齿毛刺。
    // ICO 内含 20~192 多个尺寸，这里按请求尺寸就近取图并缓存。
    private static readonly Dictionary<int, IntPtr> CardIconCache = new();
    private static readonly object CardIconLock = new();

    public static IntPtr AppHandle => LazyAppIcon.Value;
    public static IntPtr AppHandle16 => LazyAppIcon16.Value;
    public static IntPtr TrayHandle => LazyTrayIcon.Value;
    public static IntPtr EvasionTrayHandle => LazyTrayIcon.Value;

    // 按绘制的实际像素尺寸取对应位图，最多缓存 16 项（DPI 组合有限）
    private static IntPtr CardIcon(string fileName, int size)
    {
        if (size <= 0)
        {
            size = 24;
        }
        lock (CardIconLock)
        {
            if (CardIconCache.TryGetValue(HashCode.Combine(fileName, size), out var cached))
            {
                return cached;
            }
            if (CardIconCache.Count < 16)
            {
                var icon = LoadIconFromResource(fileName, size);
                CardIconCache[HashCode.Combine(fileName, size)] = icon;
                return icon;
            }
        }
        return LoadIconFromResource(fileName, size);
    }

    public static IntPtr ConnectionIcon(int size) => CardIcon("icon_conn.ico", size);
    public static IntPtr SecurityIcon(int size) => CardIcon("icon_sec.ico", size);
    public static IntPtr OptionsIcon(int size) => CardIcon("icon_opt.ico", size);

    // 应用 Logo 也按绘制尺寸取图（标题栏 48px 高，Logo 在 100%~200% 下为 24~48px）
    public static IntPtr AppIcon(int size) => CardIcon("app.ico", size);

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
