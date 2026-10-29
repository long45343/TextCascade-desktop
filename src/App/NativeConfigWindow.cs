using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 严格对齐 Windows 11 Fluent Design（系统设置规范）的纯原生 Win32 配置窗口。
// 核心特性：
//   - 单列通栏圆角大卡片（Single-Column Fluent Cards）
//   - 现代圆角矩形文本输入框（Custom Rounded Edit Boxes）
//   - 单一 Accent 高亮主按钮「保存并重连」，消除双主按钮视觉冲突
//   - 密码框浅灰占位水印「已安全保存（留空则复用）」
//   - 本地剪贴板上限单位下拉框（KB/MB/字节）与自动换算存储
//   - 顶层 Header 实时运行状态胶囊徽章（Badge）
//   - 底部状态栏自绘彩色指示灯与 WebSocket 长 URL 点击复制
//   - 表单动态联动置灰与浅灰圆角胶囊底色自适应
//   - 100% 纯原生 NativeAOT 零反射、零第三方依赖。
internal sealed class NativeConfigWindow : IDisposable
{
    private const string WindowClassName = "TextCascade_FluentConfigWndClass_v4";

    // 控件命令 ID 定义
    private const int IdServerUrl = 201;
    private const int IdUsername = 202;
    private const int IdPassword = 203;
    private const int IdSavePasswordCheck = 204;

    private const int IdCipherCheck = 211;
    private const int IdHashRounds = 212;
    private const int IdSalt = 213;
    private const int IdLocalLimit = 214;
    private const int IdUnitCombo = 215;
    private const int IdTrustCertCheck = 216;
    private const int IdCertThumbprint = 217;

    private const int IdStartupCheck = 221;
    private const int IdStatusNotificationCheck = 222;
    private const int IdUuEvasionCheck = 223;

    private const int IdBtnSaveAndReconnect = 231;
    private const int IdBtnLogout = 232;
    private const int IdBtnRestart = 233;

    private const int IdStatusLabel = 241;
    private const int IdSessionLabel = 242;
    private const int IdWebsocketLabel = 243;

    private static readonly ConcurrentDictionary<IntPtr, NativeConfigWindow> Instances = new();
    private static readonly NativeMethods.WndProc StaticWndProc = WindowProcedure;
    private static readonly object RegisterLock = new();
    private static bool _classRegistered;

    private readonly TrayHost _app;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly IntPtr _hwnd;

    // 现代字阶
    private IntPtr _fontTitle;
    private IntPtr _fontCardTitle;
    private IntPtr _fontBody;
    private IntPtr _fontCaption;
    private IntPtr _fontBadge;

    // 控件句柄
    private IntPtr _hServerUrlBox;
    private IntPtr _hUsernameBox;
    private IntPtr _hPasswordBox;
    private IntPtr _hSavePasswordCheck;

    private IntPtr _hCipherCheck;
    private IntPtr _hHashRoundsBox;
    private IntPtr _hSaltBox;
    private IntPtr _hLocalLimitBox;
    private IntPtr _hUnitCombo;
    private IntPtr _hTrustCertCheck;
    private IntPtr _hCertThumbprintBox;

    private IntPtr _hStartupCheck;
    private IntPtr _hStatusNotificationCheck;
    private IntPtr _hUuEvasionCheck;

    private IntPtr _hBtnSaveAndReconnect;
    private IntPtr _hBtnLogout;
    private IntPtr _hBtnRestart;

    private IntPtr _hSessionLabel;
    private IntPtr _hStatusLabel;
    private IntPtr _hWebsocketLabel;

    // 静态标签分类跟踪（彻底解决背景色块问题）
    private readonly HashSet<IntPtr> _cardLabels = new();
    private readonly HashSet<IntPtr> _outerLabels = new();
    private readonly HashSet<IntPtr> _allEditControls = new();

    // 输入框控件句柄与外框圆角矩形坐标关联列表
    private readonly List<(IntPtr Hwnd, NativeMethods.RECT Rect)> _editBoxes = new();

    // 界面状态与画刷缓存
    private bool _isDarkMode;
    private bool _updating;
    private bool _trustCertWarningShown;
    private bool _disposed;
    private string _currentWebsocketUrl = string.Empty;

    // GDI 资源
    private IntPtr _hBrushBg;
    private IntPtr _hBrushCard;
    private IntPtr _hBrushEditBg;
    private IntPtr _hBrushEditDisabledBg;
    private IntPtr _hPenBorder;
    private IntPtr _hPenEditBorder;
    private IntPtr _hPenEditDisabledBorder;

    // 卡片与徽章矩形坐标
    private NativeMethods.RECT _rcHeaderBadge;
    private NativeMethods.RECT _rcCard1;
    private NativeMethods.RECT _rcCard2;
    private NativeMethods.RECT _rcCard3;
    private NativeMethods.RECT _rcCardStatus;
    private NativeMethods.RECT _rcWebsocketClick;

    public NativeConfigWindow(TrayHost app)
    {
        _app = app;
        _isDarkMode = DetectIsDarkMode();

        EnsureWindowClassRegistered();
        InitializeFonts();
        UpdateThemeBrushes();

        // 现代舒展尺寸：宽 700，高 820，紧凑干练，免滚动全览
        _hwnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            "TextCascade",
            NativeMethods.WS_OVERLAPPED | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX,
            120, 60, 700, 830,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            Instances[_hwnd] = this;
            ApplyDwmAttributes();
            CreateChildControls();
            LoadFromSettings();
            RefreshFromState();
        }
    }

    public IntPtr Handle => _hwnd;

    public void Show()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNORMAL);
            NativeMethods.SetForegroundWindow(_hwnd);
        }
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        }
    }

    public void SetStatus(string message)
    {
        var text = UiText.FormatStatus(message);
        var display = string.IsNullOrWhiteSpace(text) ? UiText.Idle : text;
        if (_hStatusLabel != IntPtr.Zero)
        {
            NativeMethods.SetWindowText(_hStatusLabel, $"{UiText.Status}: {display}");
        }
    }

    public void RefreshFromState()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = _app.SettingsStore.Data;
        var loggedIn = _app.IsLoggedIn;
        var running = _app.ServiceRunning;

        var sessionText = loggedIn ? UiText.LoggedIn : UiText.NotLoggedIn;
        NativeMethods.SetWindowText(_hSessionLabel, $"{UiText.Session}: {sessionText}");

        _currentWebsocketUrl = DisplayWebsocketUrl(data.ServerUrl);
        var shortWs = _currentWebsocketUrl.Length > 42 ? _currentWebsocketUrl[..39] + "..." : _currentWebsocketUrl;
        NativeMethods.SetWindowText(_hWebsocketLabel, $"{UiText.WebSocket}: {shortWs} (点击复制)");

        NativeMethods.EnableWindow(_hBtnSaveAndReconnect, !_updating);
        NativeMethods.EnableWindow(_hBtnLogout, loggedIn && !_updating);
        NativeMethods.EnableWindow(_hBtnRestart, loggedIn && !_updating);

        // 重绘顶部状态胶囊与底部状态指示灯
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    private static string DisplayWebsocketUrl(string serverUrl)
    {
        try
        {
            return ClipConfig.WebsocketUrlFromServerUrl(serverUrl);
        }
        catch
        {
            return UiText.None;
        }
    }

    private unsafe void ApplyDwmAttributes()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        int darkModeVal = _isDarkMode ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, &darkModeVal, sizeof(int));

        int roundVal = (int)NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, &roundVal, sizeof(int));
    }

    private void InitializeFonts()
    {
        var fontFace = DetectModernFontFace();

        _fontTitle = NativeMethods.CreateFont(
            -22, 0, 0, 0, NativeMethods.FW_BOLD, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        _fontCardTitle = NativeMethods.CreateFont(
            -15, 0, 0, 0, NativeMethods.FW_SEMIBOLD, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        _fontBody = NativeMethods.CreateFont(
            -13, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        _fontCaption = NativeMethods.CreateFont(
            -11, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        _fontBadge = NativeMethods.CreateFont(
            -11, 0, 0, 0, NativeMethods.FW_SEMIBOLD, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);
    }

    private static string DetectModernFontFace()
    {
        return "Segoe UI Variable Text";
    }

    private static bool DetectIsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
            {
                return val == 0;
            }
        }
        catch
        {
        }
        return false;
    }

    private void UpdateThemeBrushes()
    {
        if (_hBrushBg != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushBg);
        if (_hBrushCard != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushCard);
        if (_hBrushEditBg != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushEditBg);
        if (_hBrushEditDisabledBg != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushEditDisabledBg);
        if (_hPenBorder != IntPtr.Zero) NativeMethods.DeleteObject(_hPenBorder);
        if (_hPenEditBorder != IntPtr.Zero) NativeMethods.DeleteObject(_hPenEditBorder);
        if (_hPenEditDisabledBorder != IntPtr.Zero) NativeMethods.DeleteObject(_hPenEditDisabledBorder);

        var bgCol = _isDarkMode ? 0x00202020U : 0x00F3F3F3U;
        var cardCol = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
        var editBgCol = _isDarkMode ? 0x00222222U : 0x00FFFFFFU;
        var editDisabledBgCol = _isDarkMode ? 0x00242424U : 0x00F4F4F4U;
        var borderCol = _isDarkMode ? 0x00383838U : 0x00E5E5E5U;
        var editBorderCol = _isDarkMode ? 0x00444444U : 0x00D1D1D1U;
        var editDisabledBorderCol = _isDarkMode ? 0x00323232U : 0x00E8E8E8U;

        _hBrushBg = NativeMethods.CreateSolidBrush(bgCol);
        _hBrushCard = NativeMethods.CreateSolidBrush(cardCol);
        _hBrushEditBg = NativeMethods.CreateSolidBrush(editBgCol);
        _hBrushEditDisabledBg = NativeMethods.CreateSolidBrush(editDisabledBgCol);
        _hPenBorder = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, borderCol);
        _hPenEditBorder = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, editBorderCol);
        _hPenEditDisabledBorder = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, editDisabledBorderCol);
    }

    private void CreateChildControls()
    {
        var cardX = 24;
        var cardW = 636;
        var editW = 280; // 紧凑优雅的输入框宽度
        var editH = 30;  // 现代舒适的输入框高度
        var editX = cardX + cardW - editW - 18;

        _editBoxes.Clear();
        _allEditControls.Clear();
        _cardLabels.Clear();
        _outerLabels.Clear();

        // ---------- 1. 顶部 Header 与运行状态胶囊徽章 ----------
        var y = 14;
        CreateLabel("TextCascade", cardX, y, 140, 28, _fontTitle, isCardInternal: false);
        _rcHeaderBadge = new NativeMethods.RECT { Left = cardX + 150, Top = y + 3, Right = cardX + 245, Bottom = y + 27 };
        y += 38;

        // ---------- 2. 卡片 1：连接与认证（就近整合保存密码） ----------
        var card1Top = y;
        var rowH = 38;
        var card1H = 32 + rowH * 3 + 28 + 8;
        _rcCard1 = new NativeMethods.RECT { Left = cardX, Top = card1Top, Right = cardX + cardW, Bottom = card1Top + card1H };

        CreateLabel(UiText.Connection, cardX + 18, card1Top + 10, 200, 20, _fontCardTitle, isCardInternal: true);

        var rowY = card1Top + 34;
        CreateLabel(UiText.ServerUrl, cardX + 20, rowY + 6, 260, 20, _fontBody, isCardInternal: true);
        _hServerUrlBox = CreateRoundedEdit(IdServerUrl, editX, rowY + 1, editW, editH);
        rowY += rowH;

        CreateLabel(UiText.Username, cardX + 20, rowY + 6, 260, 20, _fontBody, isCardInternal: true);
        _hUsernameBox = CreateRoundedEdit(IdUsername, editX, rowY + 1, editW, editH);
        rowY += rowH;

        CreateLabel(UiText.Password, cardX + 20, rowY + 6, 260, 20, _fontBody, isCardInternal: true);
        _hPasswordBox = CreateRoundedEdit(IdPassword, editX, rowY + 1, editW, editH, isPassword: true);
        rowY += rowH;

        // 保存密码复选框：直接就近置于右侧操作槽中，与上方输入框整齐对齐
        _hSavePasswordCheck = CreateCheckBox(IdSavePasswordCheck, UiText.SavePassword, editX, rowY + 1, editW, 24);

        y = _rcCard1.Bottom + 12;

        // ---------- 3. 卡片 2：安全与加密（就近整合加密与自签开关） ----------
        var card2Top = y;
        var card2H = 32 + 28 + rowH * 3 + 28 + rowH + 8;
        _rcCard2 = new NativeMethods.RECT { Left = cardX, Top = card2Top, Right = cardX + cardW, Bottom = card2Top + card2H };

        CreateLabel(UiText.SecurityAndLimits, cardX + 18, card2Top + 10, 200, 20, _fontCardTitle, isCardInternal: true);

        rowY = card2Top + 34;
        _hCipherCheck = CreateCheckBox(IdCipherCheck, UiText.EnableEncryption, cardX + 20, rowY + 2, 280, 24);
        rowY += 28;

        CreateLabel(UiText.HashRounds, cardX + 38, rowY + 6, 240, 20, _fontBody, isCardInternal: true);
        _hHashRoundsBox = CreateRoundedEdit(IdHashRounds, editX, rowY + 1, editW, editH);
        rowY += rowH;

        CreateLabel(UiText.EncryptionSalt, cardX + 38, rowY + 6, 240, 20, _fontBody, isCardInternal: true);
        _hSaltBox = CreateRoundedEdit(IdSalt, editX, rowY + 1, editW, editH);
        rowY += rowH;

        // 本地剪贴板上限：输入框 180px + 单位下拉框 90px
        CreateLabel(UiText.LocalMaxClipboardBytes, cardX + 20, rowY + 6, 260, 20, _fontBody, isCardInternal: true);
        _hLocalLimitBox = CreateRoundedEdit(IdLocalLimit, editX, rowY + 1, 180, editH);
        _hUnitCombo = CreateComboBox(IdUnitCombo, editX + 190, rowY + 1, 90, editH);
        rowY += rowH;

        _hTrustCertCheck = CreateCheckBox(IdTrustCertCheck, UiText.TrustAllCertificates, cardX + 20, rowY + 2, 280, 24);
        rowY += 28;

        CreateLabel(UiText.ServerCertificateThumbprint, cardX + 38, rowY + 6, 240, 20, _fontBody, isCardInternal: true);
        _hCertThumbprintBox = CreateRoundedEdit(IdCertThumbprint, editX, rowY + 1, editW, editH);

        y = _rcCard2.Bottom + 12;

        // ---------- 4. 卡片 3：常规与系统（仅收纳纯系统行为） ----------
        var card3Top = y;
        var card3H = 32 + 28 + 8;
        _rcCard3 = new NativeMethods.RECT { Left = cardX, Top = card3Top, Right = cardX + cardW, Bottom = card3Top + card3H };

        CreateLabel(UiText.Options, cardX + 18, card3Top + 10, 200, 20, _fontCardTitle, isCardInternal: true);

        var chkY = card3Top + 34;
        _hStartupCheck = CreateCheckBox(IdStartupCheck, UiText.StartWithWindows, cardX + 20, chkY, 190, 24);
        _hStatusNotificationCheck = CreateCheckBox(IdStatusNotificationCheck, UiText.WebSocketStatusNotification, cardX + 218, chkY, 210, 24);
        _hUuEvasionCheck = CreateCheckBox(IdUuEvasionCheck, UiText.UuEvasion, cardX + 435, chkY, 190, 24);

        y = _rcCard3.Bottom + 16;

        // ---------- 5. 核心操作栏（ActionBar：三按钮完全等宽平分各 202px，严格对齐卡片右边缘） ----------
        var btnW = 202;
        var btnGap = 15;
        _hBtnSaveAndReconnect = CreateButton(IdBtnSaveAndReconnect, UiText.SaveAndReconnect, cardX, y, btnW, 36, isAccent: true);
        _hBtnLogout = CreateButton(IdBtnLogout, UiText.Logout, cardX + btnW + btnGap, y, btnW, 36, isAccent: false);
        _hBtnRestart = CreateButton(IdBtnRestart, UiText.RestartService, cardX + (btnW + btnGap) * 2, y, btnW, 36, isAccent: false);

        y += 50;

        // ---------- 6. 底部状态卡片（上下两行全宽平衡布局） ----------
        var cardStatusTop = y;
        var cardStatusH = 72;
        _rcCardStatus = new NativeMethods.RECT { Left = cardX, Top = cardStatusTop, Right = cardX + cardW, Bottom = cardStatusTop + cardStatusH };

        var statRow1 = cardStatusTop + 13;
        var statRow2 = cardStatusTop + 39;

        // 行 1：左侧会话状态，右侧服务状态（双彩色指示灯左右对称）
        _hSessionLabel = CreateLabel("", cardX + 38, statRow1, 260, 18, _fontCaption, isCardInternal: true);
        _hStatusLabel = CreateLabel($"{UiText.Status}: {UiText.Idle}", cardX + 338, statRow1, 280, 18, _fontCaption, isCardInternal: true);

        // 行 2：通栏平铺 WebSocket 地址，点击整行一键复制
        _rcWebsocketClick = new NativeMethods.RECT { Left = cardX + 16, Top = statRow2 - 2, Right = cardX + cardW - 16, Bottom = statRow2 + 20 };
        _hWebsocketLabel = CreateLabel("", cardX + 20, statRow2, cardW - 40, 18, _fontCaption, isCardInternal: true);
    }

    private IntPtr CreateLabel(string text, int x, int y, int w, int h, IntPtr hFont, bool isCardInternal)
    {
        var hwnd = NativeMethods.CreateWindowEx(
            0,
            "STATIC",
            text,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE,
            x, y, w, h,
            _hwnd,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)hFont, 1);

        if (isCardInternal)
        {
            _cardLabels.Add(hwnd);
        }
        else
        {
            _outerLabels.Add(hwnd);
        }
        return hwnd;
    }

    private IntPtr CreateRoundedEdit(int id, int x, int y, int w, int h, bool isPassword = false)
    {
        var style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.ES_AUTOHSCROLL;
        if (isPassword)
        {
            style |= NativeMethods.ES_PASSWORD;
        }

        var hwnd = NativeMethods.CreateWindowEx(
            0,
            "EDIT",
            string.Empty,
            style,
            x + 8, y + 5, w - 16, h - 10,
            _hwnd,
            (IntPtr)id,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_fontBody, 1);

        _editBoxes.Add((hwnd, new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }));
        _allEditControls.Add(hwnd);
        return hwnd;
    }

    private IntPtr CreateComboBox(int id, int x, int y, int w, int h)
    {
        var hwnd = NativeMethods.CreateWindowEx(
            0,
            "COMBOBOX",
            string.Empty,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.CBS_DROPDOWNLIST | NativeMethods.CBS_HASSTRINGS,
            x, y + 3, w, 150,
            _hwnd,
            (IntPtr)id,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_fontBody, 1);
        NativeMethods.SendMessageString(hwnd, NativeMethods.CB_ADDSTRING, 0, "KB");
        NativeMethods.SendMessageString(hwnd, NativeMethods.CB_ADDSTRING, 0, "MB");
        NativeMethods.SendMessage(hwnd, NativeMethods.CB_SETCURSEL, 0, 0);

        _cardLabels.Add(hwnd);
        return hwnd;
    }

    private IntPtr CreateCheckBox(int id, string text, int x, int y, int w, int h)
    {
        var hwnd = NativeMethods.CreateWindowEx(
            0,
            "BUTTON",
            text,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.BS_AUTOCHECKBOX,
            x, y, w, h,
            _hwnd,
            (IntPtr)id,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_fontBody, 1);
        _cardLabels.Add(hwnd);
        return hwnd;
    }

    private IntPtr CreateButton(int id, string text, int x, int y, int w, int h, bool isAccent)
    {
        var style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP;
        if (isAccent)
        {
            style |= NativeMethods.BS_OWNERDRAW;
        }

        var hwnd = NativeMethods.CreateWindowEx(
            0,
            "BUTTON",
            text,
            style,
            x, y, w, h,
            _hwnd,
            (IntPtr)id,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_fontBody, 1);
        return hwnd;
    }

    private void LoadFromSettings()
    {
        _updating = true;
        try
        {
            var data = _app.SettingsStore.Data;
            NativeMethods.SetWindowText(_hServerUrlBox, data.ServerUrl ?? "");
            NativeMethods.SetWindowText(_hUsernameBox, data.Username ?? "");

            // 密码框水印与状态处理
            if (data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPassword))
            {
                NativeMethods.SendMessageString(_hPasswordBox, NativeMethods.EM_SETCUEBANNER, 0, "已安全保存（留空则复用）");
                NativeMethods.SetWindowText(_hPasswordBox, "");
            }
            else
            {
                NativeMethods.SendMessageString(_hPasswordBox, NativeMethods.EM_SETCUEBANNER, 0, "");
                NativeMethods.SetWindowText(_hPasswordBox, "");
            }

            NativeMethods.SetWindowText(_hHashRoundsBox, data.HashRounds.ToString());
            NativeMethods.SetWindowText(_hSaltBox, data.Salt ?? "");

            // 本地剪贴板上限单位自适应换算（仅 KB 与 MB）
            var bytes = data.LocalMaxClipboardBytes;
            if (bytes > 0 && bytes % (1024 * 1024) == 0 && bytes >= (1024 * 1024))
            {
                NativeMethods.SetWindowText(_hLocalLimitBox, (bytes / (1024 * 1024)).ToString());
                NativeMethods.SendMessage(_hUnitCombo, NativeMethods.CB_SETCURSEL, 1, 0); // MB
            }
            else
            {
                var kb = Math.Max(1, bytes / 1024);
                NativeMethods.SetWindowText(_hLocalLimitBox, kb.ToString());
                NativeMethods.SendMessage(_hUnitCombo, NativeMethods.CB_SETCURSEL, 0, 0); // KB
            }

            NativeMethods.SetWindowText(_hCertThumbprintBox, data.ServerCertificateThumbprint ?? "");

            SetCheck(_hCipherCheck, data.CipherEnabled);
            SetCheck(_hSavePasswordCheck, data.SavePassword);
            SetCheck(_hStartupCheck, data.RelaunchOnBoot);
            SetCheck(_hStatusNotificationCheck, data.WebsocketStatusNotification);
            SetCheck(_hTrustCertCheck, data.TrustAllCertificates);
            SetCheck(_hUuEvasionCheck, data.UuEvasionEnabled);

            UpdateFieldsEnableState();
        }
        finally
        {
            _updating = false;
        }
    }

    private void SaveFormSettings()
    {
        var data = _app.SettingsStore.Data;
        data.ServerUrl = GetControlText(_hServerUrlBox);
        data.Username = GetControlText(_hUsernameBox);

        if (int.TryParse(GetControlText(_hHashRoundsBox), out var rounds) && rounds > 0)
        {
            data.HashRounds = rounds;
        }
        data.Salt = GetControlText(_hSaltBox);

        // 剪贴板上限单位回算为标准字节（0 为 KB，1 为 MB）
        var limitStr = GetControlText(_hLocalLimitBox);
        if (long.TryParse(limitStr, out var limitVal) && limitVal > 0)
        {
            var curSel = (int)NativeMethods.SendMessage(_hUnitCombo, NativeMethods.CB_GETCURSEL, 0, 0);
            var multiplier = curSel == 1 ? (1024L * 1024L) : 1024L;
            data.LocalMaxClipboardBytes = limitVal * multiplier;
        }

        data.ServerCertificateThumbprint = GetControlText(_hCertThumbprintBox);

        data.CipherEnabled = IsChecked(_hCipherCheck);
        data.SavePassword = IsChecked(_hSavePasswordCheck);
        data.WebsocketStatusNotification = IsChecked(_hStatusNotificationCheck);
        data.TrustAllCertificates = IsChecked(_hTrustCertCheck);
        data.UuEvasionEnabled = IsChecked(_hUuEvasionCheck);

        var typedPassword = GetControlText(_hPasswordBox);
        if (data.SavePassword && !string.IsNullOrWhiteSpace(typedPassword))
        {
            data.SavedPassword = typedPassword;
        }
        _app.SaveSettings();
    }

    // 表单动态联动置灰与视觉状态自适应
    private void UpdateFieldsEnableState()
    {
        var cipherOn = IsChecked(_hCipherCheck);
        NativeMethods.EnableWindow(_hHashRoundsBox, cipherOn);
        NativeMethods.EnableWindow(_hSaltBox, cipherOn);

        // 反向联动：未勾选全盲信时开放指纹输入；勾选全盲信后自动置灰禁用指纹框
        var trustCertOn = IsChecked(_hTrustCertCheck);
        NativeMethods.EnableWindow(_hCertThumbprintBox, !trustCertOn);

        // 触发重绘圆角外框与内框底色
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    private static void SetCheck(IntPtr hCheck, bool isChecked)
    {
        if (hCheck != IntPtr.Zero)
        {
            NativeMethods.SendMessage(hCheck, NativeMethods.BM_SETCHECK, isChecked ? (nuint)NativeMethods.BST_CHECKED : (nuint)NativeMethods.BST_UNCHECKED, 0);
        }
    }

    private static bool IsChecked(IntPtr hCheck)
    {
        if (hCheck == IntPtr.Zero)
        {
            return false;
        }
        return NativeMethods.SendMessage(hCheck, NativeMethods.BM_GETCHECK, 0, 0) == NativeMethods.BST_CHECKED;
    }

    private static unsafe string GetControlText(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return string.Empty;
        }
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }
        fixed (char* p = new char[length + 1])
        {
            var read = NativeMethods.GetWindowText(hwnd, p, length + 1);
            return read > 0 ? new string(p, 0, read) : string.Empty;
        }
    }

    private bool ConfirmUnpinnedCertIfNecessary()
    {
        if (!IsChecked(_hTrustCertCheck) || !string.IsNullOrWhiteSpace(GetControlText(_hCertThumbprintBox)))
        {
            return true;
        }

        var result = NativeMethods.MessageBox(
            _hwnd,
            UiText.TrustCertConfirmDialogBody,
            UiText.TrustCertWarningTitle,
            NativeMethods.MB_YESNO | NativeMethods.MB_ICONWARNING | NativeMethods.MB_DEFBUTTON2);

        return result == NativeMethods.IDYES;
    }

    private static unsafe void EnsureWindowClassRegistered()
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

            fixed (char* pClassName = WindowClassName)
            {
                var wcx = new NativeMethods.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(NativeMethods.WNDCLASSEXW),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticWndProc),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = NativeMethods.GetModuleHandle(null),
                    hIcon = AppIcons.AppHandle,
                    hCursor = NativeMethods.LoadCursor(IntPtr.Zero, (nint)32512), // IDC_ARROW
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

    private static nint WindowProcedure(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
    {
        if (Instances.TryGetValue(hWnd, out var window))
        {
            return window.HandleMessage(hWnd, msg, wParam, lParam);
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private nint HandleMessage(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_PAINT:
                OnPaint();
                return 0;

            case NativeMethods.WM_CTLCOLORSTATIC:
            case NativeMethods.WM_CTLCOLORBTN:
                return OnCtlColorStatic(wParam, lParam);

            case NativeMethods.WM_CTLCOLOREDIT:
                return OnCtlColorEdit(wParam, lParam);

            case NativeMethods.WM_DRAWITEM:
                OnDrawItem(lParam);
                return 1;

            case 0x0201: // WM_LBUTTONDOWN
                var x = (int)(lParam & 0xFFFF);
                var y = (int)((lParam >> 16) & 0xFFFF);
                if (x >= _rcWebsocketClick.Left && x <= _rcWebsocketClick.Right &&
                    y >= _rcWebsocketClick.Top && y <= _rcWebsocketClick.Bottom &&
                    !string.IsNullOrWhiteSpace(_currentWebsocketUrl))
                {
                    NativeClipboard.TrySetText(_currentWebsocketUrl);
                    SetStatus(UiText.UrlCopied);
                }
                break;

            case NativeMethods.WM_SETTINGCHANGE:
                OnSettingChange();
                return 0;

            case NativeMethods.WM_CLOSE:
                LoadFromSettings();
                Hide();
                return 0;

            case NativeMethods.WM_COMMAND:
                var ctrlId = (int)(wParam & 0xFFFF);
                OnCommand(ctrlId);
                return 0;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // 绘制 Fluent 纯色底色、单列大卡片、圆角输入框与状态指示灯
    private unsafe void OnPaint()
    {
        var hdc = NativeMethods.BeginPaint(_hwnd, out var ps);
        try
        {
            // 1. 铺满窗口中性底色
            NativeMethods.FillRect(hdc, in ps.rcPaint, _hBrushBg);

            // 2. 绘制顶部 Header 运行状态胶囊徽章（Badge）
            DrawHeaderBadge(hdc, in _rcHeaderBadge);

            // 3. 绘制三大圆角卡片 + 底部状态卡片
            DrawCard(hdc, in _rcCard1);
            DrawCard(hdc, in _rcCard2);
            DrawCard(hdc, in _rcCard3);
            DrawCard(hdc, in _rcCardStatus);

            // 4. 绘制所有文本输入框的圆角矩形外框（自适应启用/禁用态）
            foreach (var (hEdit, rc) in _editBoxes)
            {
                var isEnabled = NativeMethods.IsWindowEnabled(hEdit);
                var brush = isEnabled ? _hBrushEditBg : _hBrushEditDisabledBg;
                var pen = isEnabled ? _hPenEditBorder : _hPenEditDisabledBorder;

                var prevBrush = NativeMethods.SelectObject(hdc, brush);
                var prevPen = NativeMethods.SelectObject(hdc, pen);
                NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 8, 8);
                NativeMethods.SelectObject(hdc, prevBrush);
                NativeMethods.SelectObject(hdc, prevPen);
            }

            // 5. 绘制底部状态栏发光彩色小圆点指示灯
            DrawStatusIndicatorDots(hdc);
        }
        finally
        {
            NativeMethods.EndPaint(_hwnd, in ps);
        }
    }

    private void DrawHeaderBadge(IntPtr hdc, in NativeMethods.RECT rc)
    {
        var running = _app.ServiceRunning;
        var badgeBgCol = running
            ? (_isDarkMode ? 0x00103B1EU : 0x00DFF6DDU)
            : (_isDarkMode ? 0x002A2A2AU : 0x00F0F0F0U);
        var badgeBorderCol = running ? 0x00107C41U : 0x00C0C0C0U;
        var dotCol = running ? 0x00107C41U : 0x00808080U;
        var textCol = running
            ? (_isDarkMode ? 0x0054D88CU : 0x00107C41U)
            : (_isDarkMode ? 0x009E9E9EU : 0x00666666U);

        var hBadgeBrush = NativeMethods.CreateSolidBrush(badgeBgCol);
        var hBadgePen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, badgeBorderCol);
        var prevBrush = NativeMethods.SelectObject(hdc, hBadgeBrush);
        var prevPen = NativeMethods.SelectObject(hdc, hBadgePen);

        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 12, 12);

        // 绘制指示圆点
        var hDotBrush = NativeMethods.CreateSolidBrush(dotCol);
        NativeMethods.SelectObject(hdc, hDotBrush);
        NativeMethods.Ellipse(hdc, rc.Left + 8, rc.Top + 8, rc.Left + 14, rc.Top + 14);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBadgeBrush);
        NativeMethods.DeleteObject(hBadgePen);
        NativeMethods.DeleteObject(hDotBrush);

        // 绘制徽章文字
        var badgeText = running ? "运行中" : "已停止";
        NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);
        NativeMethods.SetTextColor(hdc, textCol);
        var prevFont = NativeMethods.SelectObject(hdc, _fontBadge);
        var textRc = new NativeMethods.RECT { Left = rc.Left + 18, Top = rc.Top, Right = rc.Right - 4, Bottom = rc.Bottom };
        NativeMethods.DrawText(hdc, badgeText, badgeText.Length, ref textRc,
            NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        NativeMethods.SelectObject(hdc, prevFont);
    }

    private void DrawStatusIndicatorDots(IntPtr hdc)
    {
        var loggedIn = _app.IsLoggedIn;
        var running = _app.ServiceRunning;

        var dot1Col = loggedIn ? 0x00107C41U : 0x00808080U;
        var dot2Col = running ? 0x00107C41U : 0x00808080U;

        var row1CenterY = _rcCardStatus.Top + 22;

        // 行 1 左列指示圆点（会话状态）
        var dot1Left = _rcCardStatus.Left + 22;
        var hBrush1 = NativeMethods.CreateSolidBrush(dot1Col);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush1);
        NativeMethods.SelectObject(hdc, NativeMethods.GetStockObject(15)); // NULL_PEN
        NativeMethods.Ellipse(hdc, dot1Left, row1CenterY - 3, dot1Left + 7, row1CenterY + 4);

        // 行 1 右列指示圆点（服务运行状态）
        var dot2Left = _rcCardStatus.Left + 322;
        var hBrush2 = NativeMethods.CreateSolidBrush(dot2Col);
        NativeMethods.SelectObject(hdc, hBrush2);
        NativeMethods.Ellipse(hdc, dot2Left, row1CenterY - 3, dot2Left + 7, row1CenterY + 4);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.DeleteObject(hBrush1);
        NativeMethods.DeleteObject(hBrush2);
    }

    private void DrawCard(IntPtr hdc, in NativeMethods.RECT rc)
    {
        var prevBrush = NativeMethods.SelectObject(hdc, _hBrushCard);
        var prevPen = NativeMethods.SelectObject(hdc, _hPenBorder);

        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 16, 16);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
    }

    private nint OnCtlColorStatic(nuint wParam, nint lParam)
    {
        var hdc = (IntPtr)wParam;
        var hwndCtrl = (IntPtr)lParam;

        NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);

        if (_allEditControls.Contains(hwndCtrl))
        {
            var isEnabled = NativeMethods.IsWindowEnabled(hwndCtrl);
            var editBg = isEnabled
                ? (_isDarkMode ? 0x00222222U : 0x00FFFFFFU)
                : (_isDarkMode ? 0x00242424U : 0x00F4F4F4U);
            var editText = isEnabled
                ? (_isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU)
                : (_isDarkMode ? 0x00777777U : 0x00999999U);
            NativeMethods.SetBkColor(hdc, editBg);
            NativeMethods.SetTextColor(hdc, editText);
            return isEnabled ? _hBrushEditBg : _hBrushEditDisabledBg;
        }

        if (_cardLabels.Contains(hwndCtrl))
        {
            var textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            if (hwndCtrl == _hSessionLabel || hwndCtrl == _hWebsocketLabel || hwndCtrl == _hStatusLabel)
            {
                textCol = _isDarkMode ? 0x00A0A0A0U : 0x00666666U;
            }
            NativeMethods.SetTextColor(hdc, textCol);
            return _hBrushCard;
        }

        var outerTextCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
        NativeMethods.SetTextColor(hdc, outerTextCol);
        return _hBrushBg;
    }

    private nint OnCtlColorEdit(nuint wParam, nint lParam)
    {
        var hdc = (IntPtr)wParam;
        var hwndCtrl = (IntPtr)lParam;
        var isEnabled = NativeMethods.IsWindowEnabled(hwndCtrl);
        var editBg = isEnabled
            ? (_isDarkMode ? 0x00222222U : 0x00FFFFFFU)
            : (_isDarkMode ? 0x00242424U : 0x00F4F4F4U);
        var editText = isEnabled
            ? (_isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU)
            : (_isDarkMode ? 0x00777777U : 0x00999999U);
        NativeMethods.SetBkColor(hdc, editBg);
        NativeMethods.SetTextColor(hdc, editText);
        return isEnabled ? _hBrushEditBg : _hBrushEditDisabledBg;
    }

    // 自绘 Accent 蓝色高亮主按钮「保存并重连」
    private unsafe void OnDrawItem(nint lParam)
    {
        var pDis = (NativeMethods.DRAWITEMSTRUCT*)lParam;
        if (pDis == null)
        {
            return;
        }

        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isSelected = (pDis->itemState & NativeMethods.ODS_SELECTED) != 0;
        var isDisabled = (pDis->itemState & NativeMethods.ODS_DISABLED) != 0;

        uint btnCol;
        if (isDisabled)
        {
            btnCol = _isDarkMode ? 0x003A3A3AU : 0x00E0E0E0U;
        }
        else if (isSelected)
        {
            btnCol = 0x00B85F00U; // BGR format: #005FB8
        }
        else
        {
            btnCol = 0x00C06700U; // BGR format: #0067C0
        }

        var hBrush = NativeMethods.CreateSolidBrush(btnCol);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
        var prevPen = NativeMethods.SelectObject(hdc, _hPenBorder);

        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 12, 12);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);

        var btnText = UiText.SaveAndReconnect;
        NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);
        NativeMethods.SetTextColor(hdc, isDisabled ? 0x00888888U : 0x00FFFFFFU);

        var prevFont = NativeMethods.SelectObject(hdc, _fontBody);
        var drawRc = rc;
        NativeMethods.DrawText(hdc, btnText, btnText.Length, ref drawRc,
            NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        NativeMethods.SelectObject(hdc, prevFont);
    }

    private unsafe void OnSettingChange()
    {
        var newDarkMode = DetectIsDarkMode();
        if (newDarkMode != _isDarkMode)
        {
            _isDarkMode = newDarkMode;
            ApplyDwmAttributes();
            UpdateThemeBrushes();
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
        }
    }

    private void OnCommand(int ctrlId)
    {
        if (_updating)
        {
            return;
        }

        switch (ctrlId)
        {
            case IdBtnSaveAndReconnect:
                _ = SaveAndReconnectAsync();
                break;
            case IdBtnLogout:
                _ = LogoutAsync();
                break;
            case IdBtnRestart:
                _ = RestartServiceAsync();
                break;
            case IdCipherCheck:
                UpdateFieldsEnableState();
                break;
            case IdTrustCertCheck:
                var isTrust = IsChecked(_hTrustCertCheck);
                UpdateFieldsEnableState();
                if (isTrust && !_trustCertWarningShown)
                {
                    _trustCertWarningShown = true;
                    NativeMethods.MessageBox(
                        _hwnd,
                        UiText.TrustCertWarningBody,
                        UiText.TrustCertWarningTitle,
                        NativeMethods.MB_OK | NativeMethods.MB_ICONWARNING);
                }
                break;
        }
    }

    // 唯一的保存与重连主逻辑
    private async Task SaveAndReconnectAsync()
    {
        if (!ConfirmUnpinnedCertIfNecessary())
        {
            SetStatus(UiText.OperationCancelled);
            return;
        }

        SetBusy(true);
        SetStatus(UiText.Saving);
        try
        {
            SaveFormSettings();

            var typedPassword = GetControlText(_hPasswordBox);
            var hasPassword = !string.IsNullOrWhiteSpace(typedPassword)
                || (_app.SettingsStore.Data.SavePassword && !string.IsNullOrWhiteSpace(_app.SettingsStore.Data.SavedPassword));

            if (!hasPassword && !_app.IsLoggedIn)
            {
                SetStatus(UiText.RequiredLoginFields);
                return;
            }

            if (_app.IsLoggedIn && !hasPassword)
            {
                // 无密码且已登录：仅保存设置
                SetStatus(UiText.SaveSuccessful);
            }
            else
            {
                // 停止旧服务并使用新参数连接
                await _app.StopServiceAsync().ConfigureAwait(true);
                var rounds = int.TryParse(GetControlText(_hHashRoundsBox), out var r) && r > 0 ? r : ClipConfig.DefaultHashRounds;
                var request = new LoginRequest(
                    GetControlText(_hServerUrlBox),
                    GetControlText(_hUsernameBox),
                    hasPassword && string.IsNullOrWhiteSpace(typedPassword) ? _app.SettingsStore.Data.SavedPassword : typedPassword,
                    rounds,
                    GetControlText(_hSaltBox),
                    IsChecked(_hTrustCertCheck),
                    GetControlText(_hCertThumbprintBox));

                await _app.LoginAsync(request, _disposeCts.Token).ConfigureAwait(true);
                SetStatus(UiText.LoginSuccessful);
            }

            LoadFromSettings();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            SetStatus(UiText.SaveFailed(error.Message));
        }
        finally
        {
            SetBusy(false);
            RefreshFromState();
        }
    }

    private async Task LogoutAsync()
    {
        SetBusy(true);
        try
        {
            await _app.LogoutAsync(_disposeCts.Token).ConfigureAwait(true);
            NativeMethods.SetWindowText(_hPasswordBox, "");
            LoadFromSettings();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            SetStatus(UiText.LogoutFailed(error.Message));
        }
        finally
        {
            SetBusy(false);
            RefreshFromState();
        }
    }

    private async Task RestartServiceAsync()
    {
        SetBusy(true);
        try
        {
            await _app.RestartServiceAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            SetStatus(UiText.RestartServiceFailed(error.Message));
        }
        finally
        {
            SetBusy(false);
            RefreshFromState();
        }
    }

    private void SetBusy(bool busy)
    {
        _updating = busy;
        NativeMethods.EnableWindow(_hBtnSaveAndReconnect, !busy);
        NativeMethods.EnableWindow(_hBtnLogout, !busy && _app.IsLoggedIn);
        NativeMethods.EnableWindow(_hBtnRestart, !busy && _app.IsLoggedIn);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _disposeCts.Cancel();
        _disposeCts.Dispose();

        if (_fontTitle != IntPtr.Zero) NativeMethods.DeleteObject(_fontTitle);
        if (_fontCardTitle != IntPtr.Zero) NativeMethods.DeleteObject(_fontCardTitle);
        if (_fontBody != IntPtr.Zero) NativeMethods.DeleteObject(_fontBody);
        if (_fontCaption != IntPtr.Zero) NativeMethods.DeleteObject(_fontCaption);
        if (_fontBadge != IntPtr.Zero) NativeMethods.DeleteObject(_fontBadge);

        if (_hBrushBg != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushBg);
        if (_hBrushCard != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushCard);
        if (_hBrushEditBg != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushEditBg);
        if (_hBrushEditDisabledBg != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushEditDisabledBg);
        if (_hPenBorder != IntPtr.Zero) NativeMethods.DeleteObject(_hPenBorder);
        if (_hPenEditBorder != IntPtr.Zero) NativeMethods.DeleteObject(_hPenEditBorder);
        if (_hPenEditDisabledBorder != IntPtr.Zero) NativeMethods.DeleteObject(_hPenEditDisabledBorder);

        if (_hwnd != IntPtr.Zero)
        {
            Instances.TryRemove(_hwnd, out _);
            NativeMethods.DestroyWindow(_hwnd);
        }
    }
}
