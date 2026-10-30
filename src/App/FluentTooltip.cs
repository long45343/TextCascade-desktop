using System.Runtime.InteropServices;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 标题栏状态胶囊的非模态悬浮提示卡片。
// 复用与下拉弹层一致的 Fluent 外观（圆角面板、深浅色自适应），
// 但采用非阻塞的消息循环：鼠标悬停显示、移出延时隐藏，绝不抢焦点。
internal static class FluentTooltip
{
    private const string TipClassName = "TextCascade_FluentTooltipWndClass_v1";

    // 隐藏延时：鼠标短暂划过时不必闪现
    private const uint HideDelayMs = 260;
    private const nuint HideTimerId = 0x51;

    private static readonly NativeMethods.WndProc TipProc = TipWindowProcedure;
    private static readonly object RegisterLock = new();
    private static bool _classRegistered;

    private sealed class TipState
    {
        public IntPtr Hwnd;
        public string Title = string.Empty;
        public string[] Lines = Array.Empty<string>();
        public bool IsDarkMode;
        public IntPtr HFontTitle;
        public IntPtr HFontBody;
        public IntPtr HBrushPanel;
        public IntPtr HPenBorder;
        public double Scale = 1.0;
        public int Padding;
        public int LineGap;
        public int TitleGap;
        public int LineHeight;
    }

    // 当前唯一可见的提示（同一时刻只会有一个）
    private static TipState? _current;

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
            fixed (char* pClassName = TipClassName)
            {
                var wcx = new NativeMethods.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(NativeMethods.WNDCLASSEXW),
                    style = NativeMethods.CS_DROPSHADOW,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(TipProc),
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

    // 在屏幕坐标 (screenX, screenY) 处显示提示卡片；多次调用会替换旧卡片。
    // autoHideMs > 0 时卡片在指定毫秒后自动消失（用于一次性问题提示），
    // 0 表示不自动消失，由调用方通过 ScheduleHide/Hide 控制（用于悬停卡片）。
    public static void Show(
        IntPtr hOwner,
        int screenX,
        int screenY,
        string title,
        IReadOnlyList<string> lines,
        bool isDarkMode,
        int autoHideMs = 0)
    {
        if (hOwner == IntPtr.Zero)
        {
            return;
        }

        EnsureClassRegistered();
        Hide();

        uint dpi = NativeMethods.GetDpiForWindow(hOwner);
        if (dpi == 0) dpi = 96;
        int S(int logical) => (int)Math.Round(logical * dpi / 96.0);

        var state = new TipState
        {
            Title = title,
            Scale = dpi / 96.0,
            Lines = lines.ToArray(),
            IsDarkMode = isDarkMode,
            Padding = S(12),
            LineGap = S(4),
            TitleGap = S(7)
        };

        var hTitle = -(int)Math.Round(9.5 * dpi / 72.0);
        state.HFontTitle = NativeMethods.CreateFont(
            hTitle, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        var hBody = -(int)Math.Round(9.0 * dpi / 72.0);
        state.HFontBody = NativeMethods.CreateFont(
            hBody, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        var panelCol = isDarkMode ? 0x00303030U : 0x00FFFFFFU;
        var borderCol = isDarkMode ? 0x00505050U : 0x00DCDCDEU;
        state.HBrushPanel = NativeMethods.CreateSolidBrush(panelCol);
        state.HPenBorder = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, borderCol);

        // 量出内容尺寸
        var hdc = NativeMethods.GetDC(hOwner);
        var titleW = Measure(hdc, state.HFontTitle, title);
        var maxW = titleW;
        foreach (var line in state.Lines)
        {
            maxW = Math.Max(maxW, Measure(hdc, state.HFontBody, line));
        }
        var lineH = MeasureHeight(hdc, state.HFontBody);
        var titleH = MeasureHeight(hdc, state.HFontTitle);
        NativeMethods.ReleaseDC(hOwner, hdc);
        state.LineHeight = lineH;

        var panelW = maxW + state.Padding * 2;
        // 行高取字体实测高度 + 少量呼吸空间，避免卡片过高
        var rowH = lineH + S(2);
        var panelH = state.Padding * 2 + titleH + state.TitleGap
                     + state.Lines.Length * rowH + Math.Max(0, state.Lines.Length - 1) * state.LineGap;

        var hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_NOACTIVATE,
            TipClassName,
            string.Empty,
            NativeMethods.WS_POPUP,
            screenX, screenY, panelW, panelH,
            hOwner,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            NativeMethods.DeleteObject(state.HFontTitle);
            NativeMethods.DeleteObject(state.HFontBody);
            NativeMethods.DeleteObject(state.HBrushPanel);
            NativeMethods.DeleteObject(state.HPenBorder);
            return;
        }

        state.Hwnd = hwnd;
        _current = state;

        unsafe
        {
            var darkVal = isDarkMode ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, &darkVal, sizeof(int));
            var roundVal = (int)NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, &roundVal, sizeof(int));
        }

        // SWP_NOACTIVATE 保证提示窗永不抢焦点
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, screenX, screenY, panelW, panelH,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNA);
        NativeMethods.InvalidateRect(hwnd, IntPtr.Zero, true);

        if (autoHideMs > 0)
        {
            NativeMethods.SetTimer(hwnd, HideTimerId, (uint)autoHideMs, IntPtr.Zero);
        }
    }

    // 延时隐藏：鼠标移出后稍等再消失，避免闪烁
    public static void ScheduleHide()
    {
        if (_current is null)
        {
            return;
        }
        NativeMethods.SetTimer(_current.Hwnd, HideTimerId, HideDelayMs, IntPtr.Zero);
    }

    // 立即隐藏并释放资源
    public static void Hide()
    {
        var state = _current;
        if (state is null)
        {
            return;
        }
        _current = null;
        if (state.Hwnd != IntPtr.Zero && NativeMethods.IsWindow(state.Hwnd))
        {
            NativeMethods.KillTimer(state.Hwnd, HideTimerId);
            NativeMethods.DestroyWindow(state.Hwnd);
        }
        ReleaseResources(state);
    }

    private static void ReleaseResources(TipState state)
    {
        if (state.HFontTitle != IntPtr.Zero) NativeMethods.DeleteObject(state.HFontTitle);
        if (state.HFontBody != IntPtr.Zero) NativeMethods.DeleteObject(state.HFontBody);
        if (state.HBrushPanel != IntPtr.Zero) NativeMethods.DeleteObject(state.HBrushPanel);
        if (state.HPenBorder != IntPtr.Zero) NativeMethods.DeleteObject(state.HPenBorder);
        state.HFontTitle = IntPtr.Zero;
        state.HFontBody = IntPtr.Zero;
        state.HBrushPanel = IntPtr.Zero;
        state.HPenBorder = IntPtr.Zero;
    }

    private static int Measure(IntPtr hdc, IntPtr hFont, string text)
    {
        if (hdc == IntPtr.Zero || string.IsNullOrEmpty(text))
        {
            return 0;
        }
        var prev = NativeMethods.SelectObject(hdc, hFont);
        var ok = NativeMethods.GetTextExtentPoint32(hdc, text, text.Length, out var size);
        NativeMethods.SelectObject(hdc, prev);
        return ok ? size.cx : text.Length * 8;
    }

    private static int MeasureHeight(IntPtr hdc, IntPtr hFont)
    {
        if (hdc == IntPtr.Zero)
        {
            return 16;
        }
        var prev = NativeMethods.SelectObject(hdc, hFont);
        var ok = NativeMethods.GetTextExtentPoint32(hdc, "国Ag", 3, out var size);
        NativeMethods.SelectObject(hdc, prev);
        return ok && size.cy > 0 ? size.cy : 16;
    }

    private static nint TipWindowProcedure(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
    {
        var state = _current;
        if (state is null || state.Hwnd != hWnd)
        {
            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        switch (msg)
        {
            case NativeMethods.WM_PAINT:
                PaintTip(state);
                return 0;

            case NativeMethods.WM_TIMER:
                if (wParam == HideTimerId)
                {
                    NativeMethods.KillTimer(hWnd, HideTimerId);
                    Hide();
                }
                return 0;

            // 提示卡片不接受任何鼠标交互，点击穿透给下层窗口
            case NativeMethods.WM_MOUSEACTIVATE:
                return 3; // MA_NOACTIVATE

            case 0x0084: // WM_NCHITTEST
                return -1; // HTTRANSPARENT

            case NativeMethods.WM_DESTROY:
                return 0;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static unsafe void PaintTip(TipState state)
    {
        var hdc = NativeMethods.BeginPaint(state.Hwnd, out var ps);
        try
        {
            NativeMethods.GetClientRect(state.Hwnd, out var rcClient);
            int S(int logical) => (int)Math.Round(logical * state.Scale);

            // 圆角面板 + 1px 描边
            var prevBrush = NativeMethods.SelectObject(hdc, state.HBrushPanel);
            var prevPen = NativeMethods.SelectObject(hdc, state.HPenBorder);
            NativeMethods.RoundRect(hdc, rcClient.Left, rcClient.Top, rcClient.Right, rcClient.Bottom, S(12), S(12));
            NativeMethods.SelectObject(hdc, prevBrush);
            NativeMethods.SelectObject(hdc, prevPen);

            var panelCol = state.IsDarkMode ? 0x00303030U : 0x00FFFFFFU;
            var titleCol = state.IsDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            var bodyCol = state.IsDarkMode ? 0x00C0C0C0U : 0x00404040U;

            NativeMethods.SetBkColor(hdc, panelCol);
            NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);

            var y = state.Padding;
            var rowH = state.LineHeight + S(4);

            // 标题
            var prevFont = NativeMethods.SelectObject(hdc, state.HFontTitle);
            NativeMethods.SetTextColor(hdc, titleCol);
            var titleRc = new NativeMethods.RECT
            {
                Left = state.Padding,
                Top = y,
                Right = rcClient.Right - state.Padding,
                Bottom = y + rowH
            };
            NativeMethods.DrawText(hdc, state.Title, state.Title.Length, ref titleRc,
                NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE | NativeMethods.DT_NOPREFIX);
            NativeMethods.SelectObject(hdc, prevFont);
            y += rowH + state.TitleGap - S(4);

            // 明细行
            NativeMethods.SelectObject(hdc, state.HFontBody);
            NativeMethods.SetTextColor(hdc, bodyCol);
            foreach (var line in state.Lines)
            {
                var lineRc = new NativeMethods.RECT
                {
                    Left = state.Padding,
                    Top = y,
                    Right = rcClient.Right - state.Padding,
                    Bottom = y + rowH
                };
                NativeMethods.DrawText(hdc, line, line.Length, ref lineRc,
                    NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE | NativeMethods.DT_NOPREFIX);
                y += rowH + state.LineGap;
            }
        }
        finally
        {
            NativeMethods.EndPaint(state.Hwnd, in ps);
        }
    }

}
