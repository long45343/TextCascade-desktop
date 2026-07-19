using System.Runtime.InteropServices;

namespace TextCascadeSharp.Core;

// Windows 剪贴板监听 API 的 P/Invoke 声明。
// 使用 AddClipboardFormatListener 而非旧的 SetClipboardViewer 链：
// 新 API 不需要处理 WM_DRAWCLIPBOARD / WM_CHANGECBCHAIN，更稳定。
// 参考：https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-addclipboardformatlistener
internal static class NativeMethods
{
    // 注册窗口为剪贴板监听器。注册后窗口会收到 WM_CLIPBOARDUPDATE 消息。
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    // 取消注册。程序退出前必须调用，否则会造成系统级资源泄漏。
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
