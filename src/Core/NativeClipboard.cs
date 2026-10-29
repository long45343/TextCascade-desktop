using System.Runtime.InteropServices;

namespace TextCascadeSharp.Core;

// 基于 Win32 原生 API 封装的 Unicode 剪贴板读写实现。
// 彻底脱离 WinForms Clipboard，具备 NativeAOT 零反射、零异常开销特性。
internal static unsafe class NativeClipboard
{
    // 读取系统剪贴板中的 Unicode 文本。
    // 返回值表示是否成功访问剪贴板（false 表示被其他进程独占，触发上层退避）。
    // 若剪贴板内容非文本或为空，返回 true 且 text 为 string.Empty。
    public static bool TryGetText(out string text)
    {
        text = string.Empty;
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
        {
            return true;
        }

        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            var hData = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            if (hData == IntPtr.Zero)
            {
                return true;
            }

            var pData = NativeMethods.GlobalLock(hData);
            if (pData == IntPtr.Zero)
            {
                return true;
            }

            try
            {
                text = Marshal.PtrToStringUni(pData) ?? string.Empty;
                return true;
            }
            finally
            {
                NativeMethods.GlobalUnlock(hData);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    // 将 Unicode 文本写入系统剪贴板。
    // 返回值表示是否成功写入（false 表示内存分配失败或剪贴板被占用）。
    public static bool TrySetText(string text)
    {
        text ??= string.Empty;
        var bytesLength = (text.Length + 1) * sizeof(char);
        var hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (nuint)bytesLength);
        if (hGlobal == IntPtr.Zero)
        {
            return false;
        }

        var target = NativeMethods.GlobalLock(hGlobal);
        if (target == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(hGlobal);
            return false;
        }

        try
        {
            fixed (char* pText = text)
            {
                Buffer.MemoryCopy(pText, (void*)target, bytesLength, bytesLength);
            }
        }
        finally
        {
            NativeMethods.GlobalUnlock(hGlobal);
        }

        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            NativeMethods.GlobalFree(hGlobal);
            return false;
        }

        try
        {
            NativeMethods.EmptyClipboard();
            if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(hGlobal);
                return false;
            }
            // 写入成功后，全局内存块的所有权移交系统剪贴板托管，严禁调用 GlobalFree
            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }
}
