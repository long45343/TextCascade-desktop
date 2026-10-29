using System.Runtime.InteropServices;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 100% 纯原生 Windows 11 Fluent Design 风格模态弹窗。
// 彻底取代古老、割裂的 Win98/Win7 传统 MessageBox，具备：
//   - 动态沉浸式深浅色模式（DWMWA_USE_IMMERSIVE_DARK_MODE）；
//   - Windows 11 现代圆角（DWMWCP_ROUND）；
//   - 高级黑白主题 Accent 主按钮与极简次按钮；
//   - MiSans 矢量平滑排版与多行自适应；
//   - 模态层居中遮蔽与 Esc/Enter 快捷键支持。
internal static class FluentDialog
{
    private const string DialogClassName = "TextCascade_FluentDialogWndClass_v1";
    private static readonly NativeMethods.WndProc DialogWndProcDelegate = DialogWindowProc;
    private static readonly object RegisterLock = new();
    private static bool _classRegistered;

    private sealed class DialogState
    {
        public IntPtr Hwnd;
        public IntPtr ParentHwnd;
        public string Title = string.Empty;
        public string Message = string.Empty;
        public bool IsDarkMode;
        public bool IsConfirm; // true: 是/否 (Confirm); false: 确定 (Info)
        public bool Result;
        public IntPtr HFontTitle;
        public IntPtr HFontBody;
        public IntPtr HBrushBg;
        public IntPtr HBrushCard;
        public IntPtr HPenBorder;

        public NativeMethods.RECT BtnPrimaryRect;
        public NativeMethods.RECT BtnSecondaryRect;
        public IntPtr HBtnPrimary;
        public IntPtr HBtnSecondary;
    }

    private static readonly Dictionary<IntPtr, DialogState> DialogStates = new();

    private static unsafe void EnsureClassRegistered()
    {
        if (_classRegistered)
        {
            return;
        }
        lock (RegisterLock)
        {
            if (_classRegistered)
            {
                return;
            }

            fixed (char* pClassName = DialogClassName)
            {
                var wcx = new NativeMethods.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(NativeMethods.WNDCLASSEXW),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(DialogWndProcDelegate),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = NativeMethods.GetModuleHandle(null),
                    hIcon = IntPtr.Zero,
                    hCursor = NativeMethods.LoadCursor(IntPtr.Zero, (nint)32512),
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = pClassName,
                    hIconSm = IntPtr.Zero
                };
                NativeMethods.RegisterClassEx(wcx);
            }
            _classRegistered = true;
        }
    }

    public static bool ShowConfirm(IntPtr hParent, string title, string message, bool isDarkMode)
    {
        return ShowModal(hParent, title, message, isDarkMode, isConfirm: true);
    }

    public static void ShowInfo(IntPtr hParent, string title, string message, bool isDarkMode)
    {
        ShowModal(hParent, title, message, isDarkMode, isConfirm: false);
    }

    private static unsafe bool ShowModal(IntPtr hParent, string title, string message, bool isDarkMode, bool isConfirm)
    {
        EnsureClassRegistered();

        var state = new DialogState
        {
            ParentHwnd = hParent,
            Title = title,
            Message = message,
            IsDarkMode = isDarkMode,
            IsConfirm = isConfirm,
            Result = false
        };

        // 获取 DPI
        uint dpi = 96;
        if (hParent != IntPtr.Zero)
        {
            dpi = NativeMethods.GetDpiForWindow(hParent);
            if (dpi == 0) dpi = 96;
        }

        var hTitle = -(int)Math.Round(12.0 * dpi / 72.0);
        state.HFontTitle = NativeMethods.CreateFont(
            hTitle, 0, 0, 0, NativeMethods.FW_SEMIBOLD, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyDemibold);

        var hBody = -(int)Math.Round(9.5 * dpi / 72.0);
        state.HFontBody = NativeMethods.CreateFont(
            hBody, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        var bgCol = isDarkMode ? 0x00202020U : 0x00F3F3F3U;
        var cardCol = isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
        var borderCol = isDarkMode ? 0x00383838U : 0x00DEDEDEU;

        state.HBrushBg = NativeMethods.CreateSolidBrush(bgCol);
        state.HBrushCard = NativeMethods.CreateSolidBrush(cardCol);
        state.HPenBorder = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, borderCol);

        // 计算弹窗尺寸并居中于父窗口
        var dlgW = 440;
        var dlgH = isConfirm ? 230 : 250;

        int posX = 200, posY = 200;
        if (hParent != IntPtr.Zero && NativeMethods.GetWindowRect(hParent, out var parentRc))
        {
            var pW = parentRc.Right - parentRc.Left;
            var pH = parentRc.Bottom - parentRc.Top;
            posX = parentRc.Left + (pW - dlgW) / 2;
            posY = parentRc.Top + (pH - dlgH) / 2;
        }

        var hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOPMOST,
            DialogClassName,
            title,
            NativeMethods.WS_POPUP | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_CLIPCHILDREN,
            posX, posY, dlgW, dlgH,
            hParent,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        state.Hwnd = hwnd;
        DialogStates[hwnd] = state;

        // DWM 属性
        int darkModeVal = isDarkMode ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, &darkModeVal, sizeof(int));
        int roundVal = (int)NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, &roundVal, sizeof(int));

        CreateDialogControls(state, dlgW, dlgH);

        // 禁用父窗口（实现真正 Win32 模态）
        if (hParent != IntPtr.Zero)
        {
            NativeMethods.EnableWindow(hParent, false);
        }

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
        NativeMethods.SetForegroundWindow(hwnd);

        // 模态局部消息循环
        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == 0x0100 /* WM_KEYDOWN */)
            {
                if (msg.wParam == 0x1B /* VK_ESCAPE */)
                {
                    state.Result = false;
                    NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, 0, 0);
                    continue;
                }
                if (msg.wParam == 0x0D /* VK_RETURN */)
                {
                    state.Result = isConfirm ? false : true;
                    NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, 0, 0);
                    continue;
                }
            }

            NativeMethods.TranslateMessage(in msg);
            NativeMethods.DispatchMessage(in msg);

            if (!DialogStates.ContainsKey(hwnd))
            {
                break;
            }
        }

        // 恢复父窗口
        if (hParent != IntPtr.Zero)
        {
            NativeMethods.EnableWindow(hParent, true);
            NativeMethods.SetForegroundWindow(hParent);
        }

        // 释放 GDI
        NativeMethods.DeleteObject(state.HFontTitle);
        NativeMethods.DeleteObject(state.HFontBody);
        NativeMethods.DeleteObject(state.HBrushBg);
        NativeMethods.DeleteObject(state.HBrushCard);
        NativeMethods.DeleteObject(state.HPenBorder);

        return state.Result;
    }

    private static void CreateDialogControls(DialogState state, int dlgW, int dlgH)
    {
        var btnY = dlgH - 46 - 42;
        var btnH = 34;

        if (state.IsConfirm)
        {
            var btnW = 100;
            // 危险/次要操作：“继续”
            state.BtnSecondaryRect = new NativeMethods.RECT { Left = dlgW - 20 - btnW * 2 - 12, Top = btnY, Right = dlgW - 20 - btnW - 12, Bottom = btnY + btnH };
            state.HBtnSecondary = NativeMethods.CreateWindowEx(
                0, "BUTTON", "继续",
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.BS_OWNERDRAW,
                state.BtnSecondaryRect.Left, btnY, btnW, btnH,
                state.Hwnd, (IntPtr)1001, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
            NativeMethods.SendMessage(state.HBtnSecondary, NativeMethods.WM_SETFONT, (nuint)state.HFontBody, 1);

            // 推荐操作（默认高亮）：“取消”
            state.BtnPrimaryRect = new NativeMethods.RECT { Left = dlgW - 20 - btnW, Top = btnY, Right = dlgW - 20, Bottom = btnY + btnH };
            state.HBtnPrimary = NativeMethods.CreateWindowEx(
                0, "BUTTON", "取消",
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.BS_OWNERDRAW,
                state.BtnPrimaryRect.Left, btnY, btnW, btnH,
                state.Hwnd, (IntPtr)1002, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
            NativeMethods.SendMessage(state.HBtnPrimary, NativeMethods.WM_SETFONT, (nuint)state.HFontBody, 1);
        }
        else
        {
            var btnW = 110;
            state.BtnPrimaryRect = new NativeMethods.RECT { Left = dlgW - 20 - btnW, Top = btnY, Right = dlgW - 20, Bottom = btnY + btnH };
            state.HBtnPrimary = NativeMethods.CreateWindowEx(
                0, "BUTTON", "确定",
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.BS_OWNERDRAW,
                state.BtnPrimaryRect.Left, btnY, btnW, btnH,
                state.Hwnd, (IntPtr)1002, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
            NativeMethods.SendMessage(state.HBtnPrimary, NativeMethods.WM_SETFONT, (nuint)state.HFontBody, 1);
        }
    }

    private static unsafe nint DialogWindowProc(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
    {
        if (!DialogStates.TryGetValue(hWnd, out var state))
        {
            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        switch (msg)
        {
            case NativeMethods.WM_PAINT:
                var hdc = NativeMethods.BeginPaint(hWnd, out var ps);
                try
                {
                    NativeMethods.FillRect(hdc, in ps.rcPaint, state.HBrushBg);

                    // 1. 标题
                    NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);
                    var textCol = state.IsDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
                    NativeMethods.SetTextColor(hdc, textCol);

                    var prevFont = NativeMethods.SelectObject(hdc, state.HFontTitle);
                    var titleRc = new NativeMethods.RECT { Left = 24, Top = 20, Right = 416, Bottom = 48 };
                    NativeMethods.DrawText(hdc, state.Title, state.Title.Length, ref titleRc, NativeMethods.DT_LEFT | NativeMethods.DT_SINGLELINE);

                    // 2. 正文卡片区域
                    NativeMethods.SelectObject(hdc, state.HFontBody);
                    var subCol = state.IsDarkMode ? 0x00CCCCCCU : 0x00444444U;
                    NativeMethods.SetTextColor(hdc, subCol);

                    var msgRc = new NativeMethods.RECT { Left = 24, Top = 56, Right = 416, Bottom = 180 };
                    NativeMethods.DrawText(hdc, state.Message, state.Message.Length, ref msgRc, NativeMethods.DT_LEFT | NativeMethods.DT_WORDBREAK);

                    NativeMethods.SelectObject(hdc, prevFont);
                }
                finally
                {
                    NativeMethods.EndPaint(hWnd, in ps);
                }
                return 0;

            case NativeMethods.WM_DRAWITEM:
                var pDis = (NativeMethods.DRAWITEMSTRUCT*)lParam;
                if (pDis != null)
                {
                    DrawDialogButton(state, pDis);
                }
                return 1;

            case NativeMethods.WM_COMMAND:
                var id = (int)(wParam & 0xFFFF);
                if (id == 1001) // 继续（确认危险操作）
                {
                    state.Result = true;
                    NativeMethods.DestroyWindow(hWnd);
                }
                else if (id == 1002) // 取消 / 确定
                {
                    state.Result = !state.IsConfirm; // 确认框取消为 false，信息框确定为 true
                    NativeMethods.DestroyWindow(hWnd);
                }
                return 0;

            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(hWnd);
                return 0;

            case 0x0002 /* WM_DESTROY */:
                DialogStates.Remove(hWnd);
                return 0;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static unsafe void DrawDialogButton(DialogState state, NativeMethods.DRAWITEMSTRUCT* pDis)
    {
        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isSelected = (pDis->itemState & NativeMethods.ODS_SELECTED) != 0;
        var isPrimary = pDis->CtlID == 1002;

        uint btnCol;
        uint textCol;

        if (isPrimary)
        {
            // 黑白 Accent 主按钮
            if (state.IsDarkMode)
            {
                btnCol = isSelected ? 0x00CCCCCCU : 0x00FFFFFFU;
                textCol = 0x001A1A1AU;
            }
            else
            {
                btnCol = isSelected ? 0x00333333U : 0x001A1A1AU;
                textCol = 0x00FFFFFFU;
            }
        }
        else
        {
            // 次按钮（纯白/纯深灰圆角按钮）
            if (state.IsDarkMode)
            {
                btnCol = isSelected ? 0x00404040U : 0x002C2C2CU;
                textCol = 0x00FFFFFFU;
            }
            else
            {
                btnCol = isSelected ? 0x00E5E5E5U : 0x00FFFFFFU;
                textCol = 0x001A1A1AU;
            }
        }

        var hBrush = NativeMethods.CreateSolidBrush(btnCol);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
        var prevPen = NativeMethods.SelectObject(hdc, state.HPenBorder);

        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 8, 8);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);

        var btnText = pDis->CtlID == 1001 ? "继续" : (state.IsConfirm ? "取消" : "确定");
        NativeMethods.SetBkColor(hdc, btnCol);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, textCol);

        var prevFont = NativeMethods.SelectObject(hdc, state.HFontBody);
        var drawRc = rc;
        NativeMethods.DrawText(hdc, btnText, btnText.Length, ref drawRc,
            NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        NativeMethods.SelectObject(hdc, prevFont);
    }
}
