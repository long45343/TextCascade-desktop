using System.Runtime.InteropServices;

namespace TextCascadeSharp.Core;

// Windows 原生 API 声明集合，基于 .NET 10 [LibraryImport] 源生成器实现零 JIT 静态封送。
// 涵盖：窗口管理、原生消息循环、剪贴板、系统托盘、上下文菜单与 DPI 感知。
internal static unsafe partial class NativeMethods
{
    // ---------- 常用常量 ----------

    public const uint WM_NULL = 0x0000;
    public const uint WM_CREATE = 0x0001;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_CLIPBOARDUPDATE = 0x031D;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_SETFONT = 0x0030;
    public const uint WM_GETTEXT = 0x000D;
    public const uint WM_SETTEXT = 0x000C;
    public const uint WM_GETTEXTLENGTH = 0x000E;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_DRAWITEM = 0x002B;
    public const uint WM_CTLCOLORSTATIC = 0x0138;
    public const uint WM_CTLCOLOREDIT = 0x0133;
    public const uint WM_CTLCOLORBTN = 0x0135;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const nuint PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const nuint PBT_APMRESUMESUSPEND = 0x0007;

    // 自定义分发消息（用于 Win32SynchronizationContext 投递后台任务到主 STA 线程）
    public const uint WM_APP_DISPATCH = 0x8000 + 101;
    // 自定义托盘通知回调消息
    public const uint WM_APP_TRAYMSG = 0x8000 + 102;

    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;

    public const uint WS_OVERLAPPED = 0x00000000;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_MINIMIZE = 0x20000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_DISABLED = 0x08000000;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint WS_THICKFRAME = 0x00040000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;
    public const uint WS_TABSTOP = 0x00010000;
    public const uint WS_BORDER = 0x00800000;

    public const uint WS_EX_CLIENTEDGE = 0x00000200;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_TOPMOST = 0x00000008;

    // 控件特定样式
    public const uint ES_AUTOHSCROLL = 0x0080;
    public const uint ES_PASSWORD = 0x0020;
    public const uint EM_SETCUEBANNER = 0x1501;
    public const uint BS_CHECKBOX = 0x00000002;
    public const uint BS_AUTOCHECKBOX = 0x00000003;
    public const uint BS_DEFPUSHBUTTON = 0x00000001;
    public const uint BS_OWNERDRAW = 0x0000000B;
    public const uint BS_GROUPBOX = 0x00000007;
    public const uint BM_GETCHECK = 0x00F0;
    public const uint BM_SETCHECK = 0x00F1;
    public const nint BST_UNCHECKED = 0x0000;
    public const nint BST_CHECKED = 0x0001;

    // ComboBox 下拉框样式与消息
    public const uint CBS_DROPDOWNLIST = 0x0003;
    public const uint CBS_HASSTRINGS = 0x0200;
    public const uint CB_ADDSTRING = 0x0143;
    public const uint CB_SETCURSEL = 0x014E;
    public const uint CB_GETCURSEL = 0x0147;
    public const uint CBN_SELCHANGE = 1;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOW = 5;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIM_SETVERSION = 0x00000004;
    public const uint NOTIFYICON_VERSION_4 = 4;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_STATE = 0x00000008;
    public const uint NIF_INFO = 0x00000010;

    public const uint NIIF_NONE = 0x00000000;
    public const uint NIIF_INFO = 0x00000001;
    public const uint NIIF_WARNING = 0x00000002;
    public const uint NIIF_ERROR = 0x00000003;

    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_BOTTOMALIGN = 0x0020;
    public const uint TPM_RETURNCMD = 0x0100;

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint MF_ENABLED = 0x00000000;
    public const uint MF_GRAYED = 0x00000001;
    public const uint MF_DISABLED = 0x00000002;
    public const uint MF_UNCHECKED = 0x00000000;
    public const uint MF_CHECKED = 0x00000008;

    public const uint MB_OK = 0x00000000;
    public const uint MB_YESNO = 0x00000004;
    public const uint MB_ICONWARNING = 0x00000030;
    public const uint MB_ICONINFORMATION = 0x00000040;
    public const uint MB_DEFBUTTON2 = 0x00000100;
    public const int IDYES = 6;
    public const int IDNO = 7;

    public const int DEFAULT_GUI_FONT = 17;
    public const int COLOR_BTNFACE = 15;
    public const int COLOR_WINDOW = 5;

    public const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const uint DWMWCP_ROUND = 2;

    public const int TRANSPARENT = 1;
    public const int OPAQUE = 2;
    public const int PS_SOLID = 0;

    public const int FW_NORMAL = 400;
    public const int FW_MEDIUM = 500;
    public const int FW_SEMIBOLD = 600;
    public const int FW_BOLD = 700;
    public const uint DEFAULT_CHARSET = 1;
    public const uint GB2312_CHARSET = 134;
    public const uint CLEARTYPE_QUALITY = 5;

    public const uint DT_LEFT = 0x00000000;
    public const uint DT_CENTER = 0x00000001;
    public const uint DT_VCENTER = 0x00000004;
    public const uint DT_SINGLELINE = 0x00000020;
    public const uint DT_WORDBREAK = 0x00000010;

    public const uint ODS_SELECTED = 0x0001;
    public const uint ODS_DISABLED = 0x0004;
    public const uint ODS_FOCUS = 0x0010;

    public const nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    // ---------- 结构体与委托定义 ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRAWITEMSTRUCT
    {
        public uint CtlType;
        public uint CtlID;
        public uint itemID;
        public uint itemAction;
        public uint itemState;
        public IntPtr hwndItem;
        public IntPtr hDC;
        public RECT rcItem;
        public nuint itemData;
    }

    public delegate nint WndProc(IntPtr hWnd, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INITCOMMONCONTROLSEX
    {
        public uint dwSize;
        public uint dwICC;
    }

    // ---------- 剪贴板 API ----------

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AddClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetClipboardSequenceNumber();

    // ---------- 内存管理 API (kernel32.dll) ----------

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GlobalFree(IntPtr hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint GlobalSize(IntPtr hMem);

    // ---------- 窗口与消息循环 API (user32.dll) ----------

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in MSG lpMsg);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(IntPtr hWnd, uint Msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(IntPtr hWnd, uint uMsg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nuint SetTimer(IntPtr hWnd, nuint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(IntPtr hWnd, nuint nIDEvent);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(in WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int X,
        int Y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr GetModuleHandle(string? lpModuleName);

    // ---------- 控件与文本操作 API ----------

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(IntPtr hWnd, uint Msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SendMessageString(IntPtr hWnd, uint Msg, nuint wParam, string lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowText(IntPtr hWnd, string lpString);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
    public static partial int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    public static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowEnabled(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    // ---------- 托盘图标 API (shell32.dll) ----------

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shell_NotifyIcon(uint dwMessage, in NOTIFYICONDATAW lpData);

    // ---------- 菜单 API (user32.dll) ----------

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(IntPtr hMenu);

    [LibraryImport("user32.dll")]
    public static partial uint CheckMenuItem(IntPtr hMenu, uint uIDCheckItem, uint uCheck);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    // ---------- 图形与通用控制 API ----------

    [LibraryImport("gdi32.dll")]
    public static partial IntPtr GetStockObject(int i);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    public static partial IntPtr LoadCursor(IntPtr hInstance, nint lpCursorName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessDpiAwarenessContext(nint value);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hWnd);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitCommonControlsEx(in INITCOMMONCONTROLSEX picce);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr CreateIconFromResourceEx(
        byte* pbIconBits,
        uint cbIconBits,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVersion,
        int cxDesired,
        int cyDesired,
        uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, uint dwAttribute, void* pvAttribute, uint cbAttribute);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial IntPtr CreateSolidBrush(uint crColor);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr hObject);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetTextColor(IntPtr hdc, uint crColor);

    [LibraryImport("gdi32.dll")]
    public static partial uint SetBkColor(IntPtr hdc, uint crColor);

    [LibraryImport("gdi32.dll")]
    public static partial int SetBkMode(IntPtr hdc, int iBkMode);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RoundRect(IntPtr hdc, int x1, int y1, int x2, int y2, int cx, int cy);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial IntPtr CreatePen(int iStyle, int cWidth, uint color);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateFont(
        int cHeight,
        int cWidth,
        int cEscapement,
        int cOrientation,
        int cWeight,
        uint bItalic,
        uint bUnderline,
        uint bStrikeOut,
        uint iCharSet,
        uint iOutPrecision,
        uint iClipPrecision,
        uint iQuality,
        uint iPitchAndFamily,
        string pszFaceName);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial IntPtr AddFontMemResourceEx(
        byte* pbFont,
        uint cbFont,
        IntPtr pdv,
        out uint pcFonts);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RemoveFontMemResourceEx(IntPtr fh);

    [LibraryImport("user32.dll")]
    public static partial int FillRect(IntPtr hDC, in RECT lprc, IntPtr hbr);

    [LibraryImport("user32.dll")]
    public static partial int FrameRect(IntPtr hDC, in RECT lprc, IntPtr hbr);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawText(IntPtr hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    [LibraryImport("user32.dll")]
    public static partial IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(IntPtr hWnd, in PAINTSTRUCT lpPaint);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);
}
