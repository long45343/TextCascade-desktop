using System.Runtime.InteropServices;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 100% 纯原生 Windows 11 Fluent Design 风格模态弹窗。
// 彻底取代古老、割裂的 Win98/Win7 传统 MessageBox，具备：
//   - 无系统标题栏与图标：纯客户区圆角面板，内容与按钮全部居中，视觉干净不割裂；
//   - 动态沉浸式深浅色模式（DWMWA_USE_IMMERSIVE_DARK_MODE）；
//   - Windows 11 现代圆角（DWMWCP_ROUND）；
//   - 高级黑白主题 Accent 主按钮与极简次按钮；
//   - MiSans 矢量平滑排版与多行自适应；
//   - 模态层居中遮蔽、整窗可拖动，Esc/Enter 快捷键支持。
internal static class FluentDialog
{
    private const string DialogClassName = "TextCascade_FluentDialogWndClass_v2";
    private static readonly NativeMethods.WndProc DialogWndProcDelegate = DialogWindowProc;
    private static readonly object RegisterLock = new();
    private static bool _classRegistered;

    // 按钮命令 ID
    private const int IdBtnContinue = 1001;
    private const int IdBtnCancel = 1002;
    private const int IdBtnOk = 1003;

    private sealed class DialogState
    {
        public IntPtr Hwnd;
        public IntPtr ParentHwnd;
        public string Title = string.Empty;
        public string Message = string.Empty;
        public bool IsDarkMode;
        public bool IsConfirm; // true: 继续/取消 (Confirm); false: 确定 (Info)
        public bool Result;
        // 96dpi 逻辑像素 → 当前 DPI 物理像素的缩放系数
        public double Scale = 1.0;
        public IntPtr HFontTitle;
        public IntPtr HFontBody;
        public IntPtr HBrushBg;
        public IntPtr HPenBorder;
        // 客户区布局（供绘制与命中测试复用）
        public NativeMethods.RECT TitleRect;
        public NativeMethods.RECT BodyRect;
        public NativeMethods.RECT BtnRect;
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
                    // 无系统标题栏，因此不需要窗口图标
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

        // 弹窗跟随父窗口 DPI 缩放，与主窗口布局保持同一物理比例
        uint dpi = 96;
        if (hParent != IntPtr.Zero)
        {
            dpi = NativeMethods.GetDpiForWindow(hParent);
            if (dpi == 0) dpi = 96;
        }
        var scale = dpi / 96.0;
        int S(int logical) => (int)Math.Round(logical * scale);
        state.Scale = scale;

        var hTitle = -(int)Math.Round(12.0 * dpi / 72.0);
        state.HFontTitle = NativeMethods.CreateFont(
            hTitle, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        var hBody = -(int)Math.Round(9.5 * dpi / 72.0);
        state.HFontBody = NativeMethods.CreateFont(
            hBody, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        var bgCol = isDarkMode ? 0x00202020U : 0x00F3F3F3U;
        var borderCol = isDarkMode ? 0x00383838U : 0x00DEDEDEU;

        state.HBrushBg = NativeMethods.CreateSolidBrush(bgCol);
        state.HPenBorder = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, borderCol);

        // 无标题栏，因此尺寸全部是客户区尺寸：
        // 上留白 + 标题 + 间距 + 正文 + 间距 + 按钮 + 下留白
        var dlgW = S(420);
        var bodyWidth = dlgW - S(32) * 2;
        var titleHeight = S(26);
        var bodyHeight = MeasureMessageHeight(state, bodyWidth);
        var titleTextHeight = MeasureTitleHeight(state, bodyWidth);

        var clientH = S(26) + titleTextHeight + S(16) + bodyHeight + S(24) + S(36) + S(26);

        int posX = 200, posY = 200;
        if (hParent != IntPtr.Zero && NativeMethods.GetWindowRect(hParent, out var parentRc))
        {
            var pW = parentRc.Right - parentRc.Left;
            var pH = parentRc.Bottom - parentRc.Top;
            posX = parentRc.Left + (pW - dlgW) / 2;
            posY = parentRc.Top + (pH - clientH) / 2;
        }

        // WS_POPUP 无标题栏；工具窗口样式让其不出现在 Alt+Tab
        var hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOOLWINDOW,
            DialogClassName,
            title,
            NativeMethods.WS_POPUP | NativeMethods.WS_CLIPCHILDREN,
            posX, posY, dlgW, clientH,
            hParent,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            NativeMethods.DeleteObject(state.HFontTitle);
            NativeMethods.DeleteObject(state.HFontBody);
            NativeMethods.DeleteObject(state.HBrushBg);
            NativeMethods.DeleteObject(state.HPenBorder);
            return false;
        }

        state.Hwnd = hwnd;
        DialogStates[hwnd] = state;

        // 依据最终客户区算出正文与按钮矩形（居中）
        NativeMethods.GetClientRect(hwnd, out var rcClient);
        var clientW = rcClient.Right - rcClient.Left;
        var clientActualH = rcClient.Bottom - rcClient.Top;

        var titleTop = S(24);
        var bodyTop = titleTop + titleTextHeight + S(16);
        state.BodyRect = new NativeMethods.RECT
        {
            Left = (clientW - bodyWidth) / 2,
            Top = bodyTop,
            Right = (clientW - bodyWidth) / 2 + bodyWidth,
            Bottom = bodyTop + bodyHeight
        };
        state.BtnRect = new NativeMethods.RECT
        {
            Left = state.BodyRect.Left,
            Top = bodyTop + bodyHeight + S(24),
            Right = state.BodyRect.Right,
            Bottom = Math.Min(clientActualH - S(26), bodyTop + bodyHeight + S(24) + S(36))
        };
        state.TitleRect = new NativeMethods.RECT
        {
            Left = state.BodyRect.Left,
            Top = titleTop,
            Right = state.BodyRect.Right,
            Bottom = bodyTop - S(16)
        };

        int darkModeVal = isDarkMode ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, &darkModeVal, sizeof(int));
        int roundVal = (int)NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, &roundVal, sizeof(int));

        CreateDialogButtons(state, clientW);

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
            if (msg.message == NativeMethods.WM_KEYDOWN)
            {
                if (msg.wParam == 0x1B /* VK_ESCAPE */)
                {
                    state.Result = false;
                    NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, 0, 0);
                    continue;
                }
                if (msg.wParam == 0x0D /* VK_RETURN */)
                {
                    // 回车执行推荐动作：确认框默认「取消」（不冒险），信息框「确定」
                    state.Result = false;
                    NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, 0, 0);
                    continue;
                }
            }

            // 无标题栏时，客户区空白处按住即可拖动整个弹窗
            if (msg.message == NativeMethods.WM_LBUTTONDOWN && msg.hwnd == hwnd)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(hwnd, 0x00A1 /* WM_NCLBUTTONDOWN */, (nuint)NativeMethods.HTCAPTION, 0);
                continue;
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

        NativeMethods.DeleteObject(state.HFontTitle);
        NativeMethods.DeleteObject(state.HFontBody);
        NativeMethods.DeleteObject(state.HBrushBg);
        NativeMethods.DeleteObject(state.HPenBorder);

        return state.Result;
    }

    // 用 DT_CALCRECT 量出标题在给定宽度下的排版高度
    private static int MeasureTitleHeight(DialogState state, int width)
    {
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return (int)Math.Round(26 * state.Scale);
        }
        try
        {
            var prevFont = NativeMethods.SelectObject(hdc, state.HFontTitle);
            var rc = new NativeMethods.RECT { Left = 0, Top = 0, Right = width, Bottom = 0 };
            NativeMethods.DrawText(hdc, state.Title, state.Title.Length, ref rc,
                NativeMethods.DT_LEFT | NativeMethods.DT_WORDBREAK | NativeMethods.DT_CALCRECT);
            var measured = rc.Bottom - rc.Top;
            return measured > 0 ? measured : (int)Math.Round(26 * state.Scale);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    // 用 DT_CALCRECT 量出正文在给定宽度下的排版高度（物理像素）
    private static int MeasureMessageHeight(DialogState state, int width)
    {
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            return (int)Math.Round(120 * state.Scale);
        }
        try
        {
            var prevFont = NativeMethods.SelectObject(hdc, state.HFontBody);
            var rc = new NativeMethods.RECT { Left = 0, Top = 0, Right = width, Bottom = 0 };
            NativeMethods.DrawText(hdc, state.Message, state.Message.Length, ref rc,
                NativeMethods.DT_LEFT | NativeMethods.DT_WORDBREAK | NativeMethods.DT_CALCRECT);
            NativeMethods.SelectObject(hdc, prevFont);
            var measured = rc.Bottom - rc.Top;
            return measured > 0 ? measured : (int)Math.Round(120 * state.Scale);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    // 按钮在正文下方整行居中排列
    private static void CreateDialogButtons(DialogState state, int clientW)
    {
        int S(int logical) => (int)Math.Round(logical * state.Scale);
        var btnW = S(104);
        var btnH = state.BtnRect.Bottom - state.BtnRect.Top;
        var gap = S(16);
        var count = state.IsConfirm ? 2 : 1;
        var totalW = btnW * count + gap * (count - 1);
        var startX = (clientW - totalW) / 2;
        var btnY = state.BtnRect.Top;

        var hInstance = NativeMethods.GetModuleHandle(null);
        const uint style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.BS_OWNERDRAW;

        if (state.IsConfirm)
        {
            // 左：继续（危险操作，次按钮）
            var hContinue = NativeMethods.CreateWindowEx(0, "BUTTON", "继续", style,
                startX, btnY, btnW, btnH, state.Hwnd, (IntPtr)IdBtnContinue, hInstance, IntPtr.Zero);
            NativeMethods.SendMessage(hContinue, NativeMethods.WM_SETFONT, (nuint)state.HFontBody, 1);

            // 右：取消（推荐，主按钮高亮）
            var hCancel = NativeMethods.CreateWindowEx(0, "BUTTON", "取消", style,
                startX + btnW + gap, btnY, btnW, btnH, state.Hwnd, (IntPtr)IdBtnCancel, hInstance, IntPtr.Zero);
            NativeMethods.SendMessage(hCancel, NativeMethods.WM_SETFONT, (nuint)state.HFontBody, 1);
        }
        else
        {
            var hOk = NativeMethods.CreateWindowEx(0, "BUTTON", "确定", style,
                startX, btnY, btnW, btnH, state.Hwnd, (IntPtr)IdBtnOk, hInstance, IntPtr.Zero);
            NativeMethods.SendMessage(hOk, NativeMethods.WM_SETFONT, (nuint)state.HFontBody, 1);
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
                OnPaint(hWnd, state);
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
                switch (id)
                {
                    case IdBtnContinue:
                        state.Result = true;
                        NativeMethods.DestroyWindow(hWnd);
                        break;
                    case IdBtnOk:
                        state.Result = true;
                        NativeMethods.DestroyWindow(hWnd);
                        break;
                    case IdBtnCancel:
                        state.Result = false;
                        NativeMethods.DestroyWindow(hWnd);
                        break;
                }
                return 0;

            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(hWnd);
                return 0;

            case NativeMethods.WM_DESTROY:
                DialogStates.Remove(hWnd);
                return 0;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static unsafe void OnPaint(IntPtr hWnd, DialogState state)
    {
        var hdc = NativeMethods.BeginPaint(hWnd, out var ps);
        try
        {
            NativeMethods.FillRect(hdc, in ps.rcPaint, state.HBrushBg);

            var textCol = state.IsDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            var subCol = state.IsDarkMode ? 0x00C8C8C8U : 0x00444444U;

            // 1. 标题（居中）
            NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);
            NativeMethods.SetTextColor(hdc, textCol);
            var prevFont = NativeMethods.SelectObject(hdc, state.HFontTitle);
            var titleRc = state.TitleRect;
            NativeMethods.DrawText(hdc, state.Title, state.Title.Length, ref titleRc,
                NativeMethods.DT_CENTER | NativeMethods.DT_WORDBREAK);

            // 2. 正文（居中）
            NativeMethods.SelectObject(hdc, state.HFontBody);
            NativeMethods.SetTextColor(hdc, subCol);
            var msgRc = state.BodyRect;
            NativeMethods.DrawText(hdc, state.Message, state.Message.Length, ref msgRc,
                NativeMethods.DT_CENTER | NativeMethods.DT_WORDBREAK);

            NativeMethods.SelectObject(hdc, prevFont);
        }
        finally
        {
            NativeMethods.EndPaint(hWnd, in ps);
        }
    }

    private static unsafe void DrawDialogButton(DialogState state, NativeMethods.DRAWITEMSTRUCT* pDis)
    {
        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isSelected = (pDis->itemState & NativeMethods.ODS_SELECTED) != 0;
        // 「取消」与「确定」是推荐动作，用主按钮（黑底白字）样式
        var isPrimary = pDis->CtlID == IdBtnCancel || pDis->CtlID == IdBtnOk;

        uint btnCol;
        uint textCol;

        if (isPrimary)
        {
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

        var radius = (int)Math.Round(8 * state.Scale);
        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, radius, radius);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);

        var btnText = pDis->CtlID switch
        {
            IdBtnContinue => "继续",
            IdBtnOk => "确定",
            _ => "取消"
        };

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
