using System.Runtime.InteropServices;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 100% 纯原生 Fluent 弹出菜单，彻底取代系统原生 TrackPopupMenu。
// 原生菜单是方角、系统字体与经典勾号的 Win32 老旧外观，与本应用全自绘的圆角卡片界面严重割裂；
// 本实现提供圆角面板、Slider 高亮项、矢量对勾与深浅色自适应，视觉与窗口内下拉按钮完全统一。
internal static class FluentPopupMenu
{
    private const string PopupClassName = "TextCascade_FluentPopupWndClass_v1";

    // 逻辑尺寸（与窗口其余控件同为 96dpi 基准像素）
    // 逻辑尺寸（96dpi 基准），实际渲染时按窗口 DPI 缩放
    private const int LogicalPanelPadding = 5;
    private const int LogicalItemHeight = 30;
    private const int LogicalSeparatorHeight = 9;
    private const int LogicalCheckColumnWidth = 28;
    private const int LogicalTextPaddingRight = 14;
    private const int LogicalMinPanelWidth = 120;

    private static readonly NativeMethods.WndProc PopupProc = PopupWindowProcedure;
    private static readonly object RegisterLock = new();
    private static bool _classRegistered;

    private sealed class Item
    {
        public string Text = string.Empty;
        public bool Enabled = true;
        public bool IsSeparator;
    }

    // 96dpi 逻辑尺寸到当前 DPI 物理像素的换算结果
    private sealed class Metrics
    {
        public int PanelPadding;
        public int ItemHeight;
        public int SeparatorHeight;
        public int CheckColumnWidth;
        public int TextPaddingRight;
        public int MinPanelWidth;

        public static Metrics FromDpi(uint dpi)
        {
            var scale = dpi / 96.0;
            int S(int value) => Math.Max(1, (int)Math.Round(value * scale));
            return new Metrics
            {
                PanelPadding = S(LogicalPanelPadding),
                ItemHeight = S(LogicalItemHeight),
                SeparatorHeight = S(LogicalSeparatorHeight),
                CheckColumnWidth = S(LogicalCheckColumnWidth),
                TextPaddingRight = S(LogicalTextPaddingRight),
                MinPanelWidth = S(LogicalMinPanelWidth)
            };
        }
    }

    private sealed class PopupState
    {
        public IntPtr Hwnd;
        public Item[] Items = Array.Empty<Item>();
        public int SelectedIndex;
        public int HoverIndex = -1;
        public bool IsDarkMode;
        // DestroyWindow 会同步发出 WM_ACTIVATE(WA_INACTIVE)；用该标记区分"已选定关闭"与"失焦取消"
        public bool IsClosing;
        // 行号 → 业务项索引
        public int[] ItemIndexToRow = Array.Empty<int>();
        public Metrics Metrics = Metrics.FromDpi(96);
        public IntPtr HFont;
        public IntPtr HCheckFont;
        public IntPtr HBrushPanel;
        public IntPtr HBrushHover;
        public IntPtr HPenBorder;
        public NativeMethods.RECT[] ItemRects = Array.Empty<NativeMethods.RECT>();
        public int Result = -1;
    }

    private static readonly Dictionary<IntPtr, PopupState> States = new();

    // 下拉项定义：文本数组 + 当前选中项 + 禁用项集合
    public static int Show(
        IntPtr hOwner,
        int screenX,
        int screenY,
        int anchorWidth,
        IReadOnlyList<string> items,
        int selectedIndex,
        bool isDarkMode,
        IReadOnlySet<int>? disabledIndices = null)
    {
        return ShowCore(hOwner, screenX, screenY, anchorWidth, items, selectedIndex, isDarkMode, disabledIndices, separatorBefore: null);
    }

    // 带分隔线的菜单（服务菜单使用）：separatorBefore 中的索引之前插入一条分隔线
    public static int ShowWithSeparators(
        IntPtr hOwner,
        int screenX,
        int screenY,
        int anchorWidth,
        IReadOnlyList<string> items,
        int selectedIndex,
        bool isDarkMode,
        IReadOnlySet<int>? disabledIndices,
        IReadOnlySet<int> separatorsBefore)
    {
        return ShowCore(hOwner, screenX, screenY, anchorWidth, items, selectedIndex, isDarkMode, disabledIndices, separatorsBefore);
    }

    private static int ShowCore(
        IntPtr hOwner,
        int screenX,
        int screenY,
        int anchorWidth,
        IReadOnlyList<string> items,
        int selectedIndex,
        bool isDarkMode,
        IReadOnlySet<int>? disabledIndices,
        IReadOnlySet<int>? separatorBefore)
    {
        if (hOwner == IntPtr.Zero || items.Count == 0)
        {
            return -1;
        }

        EnsureClassRegistered();

        var list = new List<Item>(items.Count + 2);
        // 插入分隔线后行索引与业务项索引不再一致，需保留映射以便回查
        var itemIndexToRow = new int[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            if (separatorBefore is not null && separatorBefore.Contains(i) && list.Count > 0)
            {
                list.Add(new Item { IsSeparator = true });
            }
            itemIndexToRow[i] = list.Count;
            list.Add(new Item
            {
                Text = items[i],
                Enabled = disabledIndices is null || !disabledIndices.Contains(i)
            });
        }

        // 选中态同样按行号保存，避免分隔线造成高亮错位
        var selectedRow = selectedIndex >= 0 && selectedIndex < itemIndexToRow.Length
            ? itemIndexToRow[selectedIndex]
            : -1;

        var state = new PopupState
        {
            Items = list.ToArray(),
            SelectedIndex = selectedRow,
            // 键盘高亮从当前选中项出发，符合下拉框习惯（首次按 ↓ 即移到下一项）
            HoverIndex = selectedRow,
            ItemIndexToRow = itemIndexToRow,
            IsDarkMode = isDarkMode
        };

        // 弹层跟随所属窗口的 DPI 缩放，与主窗口布局保持同一物理比例
        var dpi = NativeMethods.GetDpiForWindow(hOwner);
        if (dpi == 0)
        {
            dpi = 96;
        }
        var metrics = Metrics.FromDpi(dpi);
        state.Metrics = metrics;

        var fontHeight = -(int)Math.Round(9.5 * dpi / 72.0);
        state.HFont = NativeMethods.CreateFont(
            fontHeight, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        // 勾号单独用略大的字号，让 ✓ 字形饱满填充左侧勾选列
        var checkFontHeight = -(int)Math.Round(10.5 * dpi / 72.0);
        state.HCheckFont = NativeMethods.CreateFont(
            checkFontHeight, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        var panelCol = isDarkMode ? 0x00343434U : 0x00FFFFFFU;
        var hoverCol = isDarkMode ? 0x004A4A4AU : 0x00F0F0F0U;
        var borderCol = isDarkMode ? 0x00505050U : 0x00DCDCDEU;
        state.HBrushPanel = NativeMethods.CreateSolidBrush(panelCol);
        state.HBrushHover = NativeMethods.CreateSolidBrush(hoverCol);
        state.HPenBorder = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, borderCol);

        // 面板尺寸：宽度取控件宽度与最长文本宽度的较大者
        var contentWidth = MeasureWidestText(hOwner, state, items);
        var panelW = Math.Max(
            Math.Max(anchorWidth, contentWidth + metrics.CheckColumnWidth + metrics.TextPaddingRight + metrics.PanelPadding),
            metrics.MinPanelWidth);
        var panelH = metrics.PanelPadding * 2 + MeasureItemsHeight(state);

        // 屏幕工作区边界：放不下时向上翻转，避免面板被任务栏裁切。坐标按物理像素计算。
        var anchorRc = new NativeMethods.RECT
        {
            Left = screenX,
            Top = screenY,
            Right = screenX + Math.Max(anchorWidth, 1),
            Bottom = screenY + 1
        };
        var hMonitor = NativeMethods.MonitorFromRect(in anchorRc, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var work = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (hMonitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(hMonitor, ref work))
        {
            if (screenY + panelH > work.rcWork.Bottom)
            {
                screenY = anchorRc.Top - panelH;
            }
            if (screenX + panelW > work.rcWork.Right)
            {
                screenX = Math.Max(work.rcWork.Left, work.rcWork.Right - panelW);
            }
            if (screenY < work.rcWork.Top)
            {
                screenY = work.rcWork.Top;
            }
        }

        var hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST,
            PopupClassName,
            string.Empty,
            NativeMethods.WS_POPUP,
            screenX, screenY, panelW, panelH,
            hOwner,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            ReleaseResources(state);
            return -1;
        }

        state.Hwnd = hwnd;
        state.ItemRects = BuildItemRects(state, panelW);
        States[hwnd] = state;

        unsafe
        {
            var darkModeVal = isDarkMode ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, &darkModeVal, sizeof(int));
            var roundVal = (int)NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, &roundVal, sizeof(int));
        }

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
        NativeMethods.SetForegroundWindow(hwnd);

        try
        {
            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                NativeMethods.TranslateMessage(in msg);
                NativeMethods.DispatchMessage(in msg);
                if (!States.ContainsKey(hwnd))
                {
                    break;
                }
            }
        }
        finally
        {
            // GetMessage 返回 FALSE 表示收到 WM_QUIT：此时弹层窗口不会被任何消息销毁，
            // 必须在这里兜底销毁，否则进程退出前会残留一个窗口与 States 条目
            if (States.Remove(hwnd))
            {
                NativeMethods.DestroyWindow(hwnd);
            }
            ReleaseResources(state);
        }

        return state.Result >= 0 ? RowToItemIndex(state, state.Result) : -1;
    }

    // 行号 → 业务项索引；找不到（分隔线或越界）时返回 -1
    private static int RowToItemIndex(PopupState state, int row)
    {
        for (var i = 0; i < state.ItemIndexToRow.Length; i++)
        {
            if (state.ItemIndexToRow[i] == row)
            {
                return i;
            }
        }
        return -1;
    }

    private static int MeasureWidestText(IntPtr hOwner, PopupState state, IReadOnlyList<string> items)
    {
        var hdc = NativeMethods.GetDC(hOwner);
        var widest = 0;
        if (hdc != IntPtr.Zero)
        {
            var prevFont = NativeMethods.SelectObject(hdc, state.HFont);
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item))
                {
                    continue;
                }
                if (NativeMethods.GetTextExtentPoint32(hdc, item, item.Length, out var size))
                {
                    widest = Math.Max(widest, size.cx);
                }
            }
            NativeMethods.SelectObject(hdc, prevFont);
            NativeMethods.ReleaseDC(hOwner, hdc);
        }
        // 取不到文本尺寸时按字符数粗略估算，保证面板不会过窄裁字
        if (widest == 0)
        {
            foreach (var item in items)
            {
                widest = Math.Max(widest, item.Length * 9);
            }
        }
        return widest;
    }

    private static int MeasureItemsHeight(PopupState state)
    {
        var total = 0;
        foreach (var item in state.Items)
        {
            total += item.IsSeparator ? state.Metrics.SeparatorHeight : state.Metrics.ItemHeight;
        }
        return total;
    }

    private static NativeMethods.RECT[] BuildItemRects(PopupState state, int panelW)
    {
        var metrics = state.Metrics;
        var rects = new NativeMethods.RECT[state.Items.Length];
        var y = metrics.PanelPadding;
        for (var i = 0; i < state.Items.Length; i++)
        {
            var h = state.Items[i].IsSeparator ? metrics.SeparatorHeight : metrics.ItemHeight;
            rects[i] = new NativeMethods.RECT
            {
                Left = metrics.PanelPadding,
                Top = y,
                Right = panelW - metrics.PanelPadding,
                Bottom = y + h
            };
            y += h;
        }
        return rects;
    }

    private static void ReleaseResources(PopupState state)
    {
        if (state.HFont != IntPtr.Zero) NativeMethods.DeleteObject(state.HFont);
        if (state.HCheckFont != IntPtr.Zero) NativeMethods.DeleteObject(state.HCheckFont);
        if (state.HBrushPanel != IntPtr.Zero) NativeMethods.DeleteObject(state.HBrushPanel);
        if (state.HBrushHover != IntPtr.Zero) NativeMethods.DeleteObject(state.HBrushHover);
        if (state.HPenBorder != IntPtr.Zero) NativeMethods.DeleteObject(state.HPenBorder);
        state.HFont = IntPtr.Zero;
        state.HCheckFont = IntPtr.Zero;
        state.HBrushPanel = IntPtr.Zero;
        state.HBrushHover = IntPtr.Zero;
        state.HPenBorder = IntPtr.Zero;
    }

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

            fixed (char* pClassName = PopupClassName)
            {
                var wcx = new NativeMethods.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(NativeMethods.WNDCLASSEXW),
                    style = NativeMethods.CS_DROPSHADOW,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(PopupProc),
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

    private static nint PopupWindowProcedure(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
    {
        if (!States.TryGetValue(hWnd, out var state))
        {
            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        switch (msg)
        {
            case NativeMethods.WM_PAINT:
                PaintPopup(state);
                return 0;

            case NativeMethods.WM_MOUSEMOVE:
                OnMouseMove(state, lParam);
                return 0;

            case NativeMethods.WM_LBUTTONUP:
                OnLeftButtonUp(state, lParam);
                return 0;

            case NativeMethods.WM_MOUSELEAVE:
                if (state.HoverIndex != -1)
                {
                    state.HoverIndex = -1;
                    NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                }
                return 0;

            case NativeMethods.WM_KEYDOWN:
                OnKeyDown(state, (int)wParam);
                return 0;

            case NativeMethods.WM_ACTIVATE:
                if ((int)(wParam & 0xFFFF) == NativeMethods.WA_INACTIVE && !state.IsClosing)
                {
                    state.Result = -1;
                    state.IsClosing = true;
                    NativeMethods.DestroyWindow(hWnd);
                }
                return 0;

            case NativeMethods.WM_MOUSEACTIVATE:
                return NativeMethods.MA_ACTIVATE;

            case NativeMethods.WM_DESTROY:
                States.Remove(hWnd);
                return 0;

            case NativeMethods.WM_CLOSE:
                state.Result = -1;
                state.IsClosing = true;
                NativeMethods.DestroyWindow(hWnd);
                return 0;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void OnMouseMove(PopupState state, nint lParam)
    {
        var x = (short)(lParam & 0xFFFF);
        var y = (short)((lParam >> 16) & 0xFFFF);
        var index = HitTest(state, x, y);

        var track = new NativeMethods.TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
            dwFlags = NativeMethods.TME_LEAVE,
            hwndTrack = state.Hwnd,
            dwHoverTime = 0
        };
        NativeMethods.TrackMouseEvent(ref track);

        if (index != state.HoverIndex)
        {
            state.HoverIndex = index;
            NativeMethods.InvalidateRect(state.Hwnd, IntPtr.Zero, false);
        }
    }

    private static void OnLeftButtonUp(PopupState state, nint lParam)
    {
        var x = (short)(lParam & 0xFFFF);
        var y = (short)((lParam >> 16) & 0xFFFF);
        var index = HitTest(state, x, y);
        if (index >= 0)
        {
            Choose(state, index);
        }
    }

    private static void OnKeyDown(PopupState state, int virtualKey)
    {
        switch (virtualKey)
        {
            case NativeMethods.VK_ESCAPE:
                state.Result = -1;
                NativeMethods.DestroyWindow(state.Hwnd);
                break;

            case NativeMethods.VK_RETURN:
                if (state.HoverIndex >= 0)
                {
                    Choose(state, state.HoverIndex);
                }
                else if (state.SelectedIndex >= 0 && state.SelectedIndex < state.Items.Length)
                {
                    Choose(state, state.SelectedIndex);
                }
                break;

            case NativeMethods.VK_UP:
                MoveHover(state, -1);
                break;

            case NativeMethods.VK_DOWN:
                MoveHover(state, 1);
                break;

            case NativeMethods.VK_HOME:
                state.HoverIndex = FirstSelectable(state);
                NativeMethods.InvalidateRect(state.Hwnd, IntPtr.Zero, false);
                break;

            case NativeMethods.VK_END:
                state.HoverIndex = LastSelectable(state);
                NativeMethods.InvalidateRect(state.Hwnd, IntPtr.Zero, false);
                break;
        }
    }

    private static void MoveHover(PopupState state, int step)
    {
        var count = state.Items.Length;
        var start = state.HoverIndex < 0
            ? (step > 0 ? -1 : count)
            : state.HoverIndex;
        var index = start;
        for (var i = 0; i < count; i++)
        {
            index += step;
            if (index < 0)
            {
                index = count - 1;
            }
            else if (index >= count)
            {
                index = 0;
            }
            if (!state.Items[index].IsSeparator && state.Items[index].Enabled)
            {
                state.HoverIndex = index;
                NativeMethods.InvalidateRect(state.Hwnd, IntPtr.Zero, false);
                return;
            }
        }
    }

    private static int FirstSelectable(PopupState state)
    {
        for (var i = 0; i < state.Items.Length; i++)
        {
            if (!state.Items[i].IsSeparator && state.Items[i].Enabled)
            {
                return i;
            }
        }
        return -1;
    }

    private static int LastSelectable(PopupState state)
    {
        for (var i = state.Items.Length - 1; i >= 0; i--)
        {
            if (!state.Items[i].IsSeparator && state.Items[i].Enabled)
            {
                return i;
            }
        }
        return -1;
    }

    private static void Choose(PopupState state, int index)
    {
        if (index < 0 || index >= state.Items.Length)
        {
            return;
        }
        var item = state.Items[index];
        if (item.IsSeparator || !item.Enabled)
        {
            return;
        }
        state.Result = index;
        state.IsClosing = true;
        NativeMethods.DestroyWindow(state.Hwnd);
    }

    private static int HitTest(PopupState state, int x, int y)
    {
        for (var i = 0; i < state.ItemRects.Length; i++)
        {
            var rc = state.ItemRects[i];
            if (x >= rc.Left && x < rc.Right && y >= rc.Top && y < rc.Bottom)
            {
                var item = state.Items[i];
                return item.IsSeparator || !item.Enabled ? -1 : i;
            }
        }
        return -1;
    }

    private static unsafe void PaintPopup(PopupState state)
    {
        var hdc = NativeMethods.BeginPaint(state.Hwnd, out var ps);
        try
        {
            NativeMethods.GetClientRect(state.Hwnd, out var rcClient);
            var metrics = state.Metrics;

            // 面板底：圆角实心填充 + 1px 描边
            var prevBrush = NativeMethods.SelectObject(hdc, state.HBrushPanel);
            var prevPen = NativeMethods.SelectObject(hdc, state.HPenBorder);
            NativeMethods.RoundRect(hdc, rcClient.Left, rcClient.Top, rcClient.Right, rcClient.Bottom, 12, 12);
            NativeMethods.SelectObject(hdc, prevBrush);
            NativeMethods.SelectObject(hdc, prevPen);

            var textCol = state.IsDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            var disabledTextCol = state.IsDarkMode ? 0x00777777U : 0x00A0A0A0U;
            var checkCol = state.IsDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            var panelCol = state.IsDarkMode ? 0x00343434U : 0x00FFFFFFU;
            var hoverCol = state.IsDarkMode ? 0x004A4A4AU : 0x00F0F0F0U;

            for (var i = 0; i < state.Items.Length; i++)
            {
                var item = state.Items[i];
                var rc = state.ItemRects[i];

                if (item.IsSeparator)
                {
                    var sepCol = state.IsDarkMode ? 0x00404040U : 0x00E6E6E6U;
                    var sepPen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, sepCol);
                    var prevSepPen = NativeMethods.SelectObject(hdc, sepPen);
                    var midY = (rc.Top + rc.Bottom) / 2;
                    NativeMethods.MoveToEx(hdc, rc.Left + metrics.PanelPadding, midY, IntPtr.Zero);
                    NativeMethods.LineTo(hdc, rc.Right - metrics.PanelPadding, midY);
                    NativeMethods.SelectObject(hdc, prevSepPen);
                    NativeMethods.DeleteObject(sepPen);
                    continue;
                }

                // 悬停高亮：圆角滑块式浅色背景
                if (i == state.HoverIndex && item.Enabled)
                {
                    var prevHoverBrush = NativeMethods.SelectObject(hdc, state.HBrushHover);
                    var prevHoverPen = NativeMethods.SelectObject(hdc, NativeMethods.GetStockObject(NativeMethods.NULL_PEN));
                    NativeMethods.RoundRect(hdc, rc.Left + 2, rc.Top + 1, rc.Right - 2, rc.Bottom - 1, 8, 8);
                    NativeMethods.SelectObject(hdc, prevHoverBrush);
                    NativeMethods.SelectObject(hdc, prevHoverPen);
                }

                // 选中项对勾：MiSans 的 ✓ 字形，与界面内复选框同源，避免 GDI 折线的锯齿感
                if (i == state.SelectedIndex && item.Enabled)
                {
                    var prevCheckFont = NativeMethods.SelectObject(hdc, state.HCheckFont);
                    NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);
                    NativeMethods.SetTextColor(hdc, checkCol);
                    var checkRc = new NativeMethods.RECT
                    {
                        Left = rc.Left + metrics.PanelPadding,
                        Top = rc.Top,
                        Right = rc.Left + metrics.CheckColumnWidth,
                        Bottom = rc.Bottom
                    };
                    const string glyph = "\u2713";
                    NativeMethods.DrawText(hdc, glyph, glyph.Length, ref checkRc,
                        NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
                    NativeMethods.SelectObject(hdc, prevCheckFont);
                }

                // 文字背景必须与当前行底色一致：否则 OPAQUE 绘制会在悬停高亮上盖出白底方块
                var rowBg = (i == state.HoverIndex && item.Enabled) ? hoverCol : panelCol;
                NativeMethods.SetBkColor(hdc, rowBg);
                NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
                NativeMethods.SetTextColor(hdc, item.Enabled ? textCol : disabledTextCol);

                var prevFont = NativeMethods.SelectObject(hdc, state.HFont);
                var textRc = new NativeMethods.RECT
                {
                    Left = rc.Left + metrics.CheckColumnWidth,
                    Top = rc.Top,
                    Right = rc.Right - metrics.TextPaddingRight,
                    Bottom = rc.Bottom
                };
                NativeMethods.DrawText(hdc, item.Text, item.Text.Length, ref textRc,
                    NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
                NativeMethods.SelectObject(hdc, prevFont);
            }
        }
        finally
        {
            NativeMethods.EndPaint(state.Hwnd, in ps);
        }
    }
}
