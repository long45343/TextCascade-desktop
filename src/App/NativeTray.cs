using System.Runtime.InteropServices;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 基于纯 Win32 原生 Shell_NotifyIcon 与菜单 API 封装的系统托盘。
// 彻底脱离 System.Windows.Forms.NotifyIcon，天然兼容 NativeAOT。
internal sealed unsafe class NativeTray : IDisposable
{
    private const uint TrayIconId = 1;

    // 菜单命令 ID
    public const nuint IdmShowWindow = 1001;
    public const nuint IdmRestartService = 1002;
    public const nuint IdmLogout = 1003;
    public const nuint IdmExit = 1004;

    private readonly IntPtr _hwnd;
    private readonly Action _onShow;
    private readonly Action _onRestart;
    private readonly Action _onLogout;
    private readonly Action _onExit;
    private readonly Func<bool> _isLoggedIn;
    private NativeMethods.NOTIFYICONDATAW _nid;
    private bool _added;
    private bool _disposed;

    public NativeTray(
        IntPtr hwnd,
        Action onShow,
        Action onRestart,
        Action onLogout,
        Action onExit,
        Func<bool> isLoggedIn)
    {
        _hwnd = hwnd;
        _onShow = onShow;
        _onRestart = onRestart;
        _onLogout = onLogout;
        _onExit = onExit;
        _isLoggedIn = isLoggedIn;

        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        _nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NativeMethods.NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_APP_TRAYMSG,
            hIcon = AppIcons.TrayHandle
        };

        SetTipText("TextCascade");

        _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, in _nid);
        if (_added)
        {
            _nid.uTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, in _nid);
        }
    }

    // 设置托盘悬停提示文本
    public void SetTip(string tip)
    {
        if (_disposed || !_added)
        {
            return;
        }

        _nid.uFlags = NativeMethods.NIF_TIP;
        SetTipText(tip);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, in _nid);
    }

    // 切换托盘图标（正常 vs 规避灰度）
    public void SetIcon(IntPtr hIcon)
    {
        if (_disposed || !_added || hIcon == IntPtr.Zero)
        {
            return;
        }

        _nid.uFlags = NativeMethods.NIF_ICON;
        _nid.hIcon = hIcon;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, in _nid);
    }

    // 弹出系统原生气泡通知
    public void ShowBalloon(string title, string text, uint infoFlags = NativeMethods.NIIF_INFO)
    {
        if (_disposed || !_added)
        {
            return;
        }

        _nid.uFlags = NativeMethods.NIF_INFO;
        _nid.dwInfoFlags = infoFlags;
        fixed (char* pTitle = _nid.szInfoTitle)
        {
            CopyToFixed(title, pTitle, 64);
        }
        fixed (char* pText = _nid.szInfo)
        {
            CopyToFixed(text, pText, 256);
        }
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, in _nid);
    }

    // 处理来自主窗口的托盘通知消息
    public void HandleTrayMessage(nint lParam)
    {
        var msg = (uint)(lParam & 0xFFFF);
        switch (msg)
        {
            case NativeMethods.WM_LBUTTONDBLCLK:
                _onShow();
                break;

            case NativeMethods.WM_RBUTTONUP:
            case NativeMethods.WM_CONTEXTMENU:
                ShowContextMenu();
                break;
        }
    }

    // 弹出纯原生 Win32 右键菜单
    private void ShowContextMenu()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, IdmShowWindow, UiText.Show);
            var restartFlags = _isLoggedIn() ? NativeMethods.MF_STRING : (NativeMethods.MF_STRING | NativeMethods.MF_GRAYED);
            NativeMethods.AppendMenu(hMenu, restartFlags, IdmRestartService, UiText.RestartService);
            var logoutFlags = _isLoggedIn() ? NativeMethods.MF_STRING : (NativeMethods.MF_STRING | NativeMethods.MF_GRAYED);
            NativeMethods.AppendMenu(hMenu, logoutFlags, IdmLogout, UiText.Logout);
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, string.Empty);
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, IdmExit, UiText.Exit);

            NativeMethods.GetCursorPos(out var pt);

            // Windows 托盘弹出菜单规范：必须先 SetForegroundWindow，弹完后投递 WM_NULL，防止菜单无法失焦关闭
            NativeMethods.SetForegroundWindow(_hwnd);
            var cmd = NativeMethods.TrackPopupMenuEx(
                hMenu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_LEFTALIGN | NativeMethods.TPM_BOTTOMALIGN,
                pt.X,
                pt.Y,
                _hwnd,
                IntPtr.Zero);
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_NULL, 0, 0);

            switch (cmd)
            {
                case (uint)IdmShowWindow:
                    _onShow();
                    break;
                case (uint)IdmRestartService:
                    _onRestart();
                    break;
                case (uint)IdmLogout:
                    _onLogout();
                    break;
                case (uint)IdmExit:
                    _onExit();
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    private void SetTipText(string tip)
    {
        fixed (char* pTip = _nid.szTip)
        {
            CopyToFixed(tip, pTip, 128);
        }
    }

    private static void CopyToFixed(string text, char* dest, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            dest[0] = '\0';
            return;
        }

        var len = Math.Min(text.Length, maxChars - 1);
        for (var i = 0; i < len; i++)
        {
            dest[i] = text[i];
        }
        dest[len] = '\0';
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_added)
        {
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, in _nid);
            _added = false;
        }
    }
}
