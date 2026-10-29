using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 严格参考 HTML 紧凑栅格排版 + 纯正 Windows 11 Fluent Design 原生质感的 Win32 配置窗口。
// 核心排版与视觉设计：
//   - 进一步收紧横向至 540px/496px，彻底消除任何空洞留白；
//   - 每个大标题左侧嵌入原生矢量 Logo（连接：链条；安全：盾牌；设置：齿轮）；
//   - 剪贴板上限纯净无“单条内容”后缀；证书校验三模式（自签弹窗警告）；UU远程规避；服务菜单收纳暂停与详情；
//   - 删除大标题、删除“已保存的密码将继续使用”、删除底部长 WebSocket 链接并收缩留白至 46px 紧凑状态栏；
//   - 100% 纯原生 NativeAOT 零反射、零第三方依赖，秒级冷启。
internal sealed class NativeConfigWindow : IDisposable
{
    private const string WindowClassName = "TextCascade_FluentConfigWndClass_v8";

    // 控件命令 ID 定义
    private const int IdServerUrl = 201;
    private const int IdUsername = 202;
    private const int IdBtnAccount = 203;
    private const int IdPassword = 204;
    private const int IdBtnChangePassword = 205;
    private const int IdSavePasswordCheck = 206;

    private const int IdLocalLimit = 211;
    private const int IdUnitCombo = 212;
    private const int IdCipherCheck = 213;
    private const int IdHashRounds = 214;
    private const int IdSalt = 215;
    private const int IdCertModeCombo = 216;
    private const int IdCertThumbprint = 217;

    private const int IdStartupCheck = 221;
    private const int IdStatusNotificationCheck = 222;
    private const int IdUuEvasionCheck = 223;

    private const int IdBtnServiceMenu = 231;
    private const int IdBtnClose = 232;
    private const int IdBtnSaveAndReconnect = 233;

    // 「服务 ▾」菜单项 ID
    private const nuint IdmMenuRestart = 101;
    private const nuint IdmMenuTogglePause = 102;
    private const nuint IdmMenuConnectionDetails = 103;

    private static readonly ConcurrentDictionary<IntPtr, NativeConfigWindow> Instances = new();
    private static readonly NativeMethods.WndProc StaticWndProc = WindowProcedure;
    private static readonly object RegisterLock = new();
    private static bool _classRegistered;

    private readonly TrayHost _app;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly IntPtr _hwnd;

    // 现代字阶
    private IntPtr _fontCardTitle;
    private IntPtr _fontBody;
    private IntPtr _fontCaption;

    // 控件句柄
    private IntPtr _hServerUrlBox;
    private IntPtr _hUsernameBox;
    private IntPtr _hBtnAccount;
    private IntPtr _hPasswordBox;
    private IntPtr _hBtnChangePassword;
    private IntPtr _hSavePasswordCheck;

    private IntPtr _hLocalLimitBox;
    private IntPtr _hUnitCombo;
    private IntPtr _hCipherCheck;
    private IntPtr _hHashRoundsBox;
    private IntPtr _hSaltBox;
    private IntPtr _hCertModeCombo;
    private IntPtr _hCertThumbprintLabel;
    private IntPtr _hCertThumbprintBox;

    private IntPtr _hStartupCheck;
    private IntPtr _hStatusNotificationCheck;
    private IntPtr _hUuEvasionCheck;

    private IntPtr _hBtnServiceMenu;
    private IntPtr _hBtnClose;
    private IntPtr _hBtnSaveAndReconnect;

    private IntPtr _hSessionLabel;
    private IntPtr _hServiceLabel;

    // 静态标签分类跟踪
    private readonly HashSet<IntPtr> _cardLabels = new();
    private readonly HashSet<IntPtr> _allEditControls = new();

    // 输入框控件句柄与外框圆角矩形坐标关联列表
    private readonly List<(IntPtr Hwnd, NativeMethods.RECT Rect)> _editBoxes = new();

    // 界面状态与画刷缓存
    private bool _isDarkMode;
    private bool _updating;
    private bool _isEditingPassword;
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

    // 卡片矩形坐标
    private NativeMethods.RECT _rcCard1;
    private NativeMethods.RECT _rcCard2;
    private NativeMethods.RECT _rcCard3;
    private NativeMethods.RECT _rcCardStatus;

    public NativeConfigWindow(TrayHost app)
    {
        _app = app;
        _isDarkMode = DetectIsDarkMode();

        EnsureWindowClassRegistered();
        InitializeFonts();
        UpdateThemeBrushes();

        // 进一步收紧横向：宽 540，高 665，紧凑精巧
        _hwnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            "TextCascade",
            NativeMethods.WS_OVERLAPPED | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_CLIPCHILDREN,
            150, 70, 540, 665,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            Instances[_hwnd] = this;
            InitializeFonts();
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
        var paused = _app.IsPaused;

        // 底部紧凑状态栏展示
        NativeMethods.SetWindowText(_hSessionLabel, $"{UiText.Session}: {(loggedIn ? UiText.LoggedIn : UiText.NotLoggedIn)}");
        var serviceStateText = running ? (paused ? "运行中 (已暂停)" : UiText.Running) : UiText.Stopped;
        NativeMethods.SetWindowText(_hServiceLabel, $"{UiText.Service}: {serviceStateText}");

        _currentWebsocketUrl = DisplayWebsocketUrl(data.ServerUrl);

        NativeMethods.SetWindowText(_hBtnAccount, loggedIn ? UiText.Logout : UiText.Login);
        NativeMethods.EnableWindow(_hBtnAccount, !_updating);
        NativeMethods.EnableWindow(_hBtnSaveAndReconnect, !_updating);

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
        uint dpi = 96;
        if (_hwnd != IntPtr.Zero)
        {
            dpi = NativeMethods.GetDpiForWindow(_hwnd);
        }
        if (dpi == 0)
        {
            dpi = 96;
        }

        if (_fontCardTitle != IntPtr.Zero) NativeMethods.DeleteObject(_fontCardTitle);
        if (_fontBody != IntPtr.Zero) NativeMethods.DeleteObject(_fontBody);
        if (_fontCaption != IntPtr.Zero) NativeMethods.DeleteObject(_fontCaption);

        // 卡片标题：绑定 MiSans Demibold 真半粗体，cWeight 设为 400 避免 GDI 算法重影
        var hCardTitle = -PtToPixelHeight(11.5, dpi);
        _fontCardTitle = NativeMethods.CreateFont(
            hCardTitle, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyDemibold);

        // 控件正文：9.5pt，使用 MiSans Normal 真实矢量轮廓
        var hBody = -PtToPixelHeight(9.5, dpi);
        _fontBody = NativeMethods.CreateFont(
            hBody, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        // 辅助说明：8.5pt
        var hCaption = -PtToPixelHeight(8.5, dpi);
        _fontCaption = NativeMethods.CreateFont(
            hCaption, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);
    }

    private static int PtToPixelHeight(double pt, uint dpi)
    {
        return (int)Math.Round(pt * dpi / 72.0);
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
        var editDisabledBgCol = _isDarkMode ? 0x00262626U : 0x00F4F4F4U;
        var borderCol = _isDarkMode ? 0x00383838U : 0x00DEDEDEU;
        var editBorderCol = _isDarkMode ? 0x00444444U : 0x00D1D1D1U;
        var editDisabledBorderCol = _isDarkMode ? 0x00323232U : 0x00E2E2E2U;

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
        var cardX = 14;
        var cardW = 496; // 进一步收紧卡片宽度至 496px
        var labelW = 100; // 紧凑标签宽度
        var ctrlX = cardX + 16 + labelW + 8; // 138px
        var ctrlW = cardX + cardW - 14 - ctrlX; // 358px

        _editBoxes.Clear();
        _allEditControls.Clear();
        _cardLabels.Clear();

        var y = 14;

        // ---------- 1. 卡片 1：连接与认证（带链条 Logo） ----------
        var card1Top = y;
        var rowH = 36;
        var card1H = 36 + rowH * 3 + 28 + 8;
        _rcCard1 = new NativeMethods.RECT { Left = cardX, Top = card1Top, Right = cardX + cardW, Bottom = card1Top + card1H };

        // 标题文字紧挨着左侧 18x18 的 Logo
        CreateLabel(UiText.Connection, cardX + 40, card1Top + 8, 120, 22, _fontCardTitle, isCardInternal: true);

        var rowY = card1Top + 36;
        CreateLabel(UiText.ServerUrl, cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hServerUrlBox = CreateRoundedEdit(IdServerUrl, ctrlX, rowY + 1, ctrlW, 28);
        rowY += rowH;

        CreateLabel(UiText.Username, cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        var userEditW = ctrlW - 88;
        _hUsernameBox = CreateRoundedEdit(IdUsername, ctrlX, rowY + 1, userEditW, 28);
        _hBtnAccount = CreateButton(IdBtnAccount, UiText.Logout, ctrlX + userEditW + 8, rowY + 1, 80, 28, isAccent: false);
        rowY += rowH;

        CreateLabel(UiText.Password, cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        var pwdEditW = ctrlW - 88;
        _hPasswordBox = CreateRoundedEdit(IdPassword, ctrlX, rowY + 1, pwdEditW, 28, isPassword: true);
        _hBtnChangePassword = CreateButton(IdBtnChangePassword, "更换", ctrlX + pwdEditW + 8, rowY + 1, 80, 28, isAccent: false);
        rowY += rowH;

        // 保存密码（无任何多余小字！）
        _hSavePasswordCheck = CreateCheckBox(IdSavePasswordCheck, UiText.SavePassword, ctrlX, rowY + 3, 160, 22);

        y = _rcCard1.Bottom + 12;

        // ---------- 2. 卡片 2：安全与限制（带盾牌 Logo） ----------
        var card2Top = y;
        var card2H = 36 + rowH * 5 + 8;
        _rcCard2 = new NativeMethods.RECT { Left = cardX, Top = card2Top, Right = cardX + cardW, Bottom = card2Top + card2H };

        CreateLabel(UiText.SecurityAndLimits, cardX + 40, card2Top + 8, 130, 22, _fontCardTitle, isCardInternal: true);

        rowY = card2Top + 36;
        CreateLabel(UiText.LocalMaxClipboardBytes, cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hLocalLimitBox = CreateRoundedEdit(IdLocalLimit, ctrlX, rowY + 1, 95, 28);
        _hUnitCombo = CreateComboBox(IdUnitCombo, ctrlX + 103, rowY + 1, 72, 28);
        rowY += rowH;

        CreateLabel("内容加密", cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hCipherCheck = CreateCheckBox(IdCipherCheck, UiText.EnableEncryption, ctrlX, rowY + 3, 140, 22);
        rowY += rowH;

        CreateLabel(UiText.HashRounds, cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hHashRoundsBox = CreateRoundedEdit(IdHashRounds, ctrlX, rowY + 1, 65, 28);
        CreateLabel(UiText.EncryptionSalt, ctrlX + 73, rowY + 5, 48, 20, _fontBody, isCardInternal: true);
        var saltW = ctrlW - 129;
        _hSaltBox = CreateRoundedEdit(IdSalt, ctrlX + 129, rowY + 1, saltW, 28);
        rowY += rowH;

        CreateLabel("证书校验", cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hCertModeCombo = CreateCertModeComboBox(IdCertModeCombo, ctrlX, rowY + 1, 215, 28);
        rowY += rowH;

        // 证书 SHA-256（彻底无括号！）
        _hCertThumbprintLabel = CreateLabel(UiText.ServerCertificateThumbprint, cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hCertThumbprintBox = CreateRoundedEdit(IdCertThumbprint, ctrlX, rowY + 1, ctrlW, 28);

        y = _rcCard2.Bottom + 12;

        // ---------- 3. 卡片 3：选项（带齿轮 Logo，单行横排） ----------
        var card3Top = y;
        var card3H = 36 + 32 + 8;
        _rcCard3 = new NativeMethods.RECT { Left = cardX, Top = card3Top, Right = cardX + cardW, Bottom = card3Top + card3H };

        CreateLabel(UiText.Options, cardX + 40, card3Top + 8, 120, 22, _fontCardTitle, isCardInternal: true);

        var chkY = card3Top + 36;
        _hStartupCheck = CreateCheckBox(IdStartupCheck, UiText.StartWithWindows, cardX + 14, chkY, 95, 24);
        _hStatusNotificationCheck = CreateCheckBox(IdStatusNotificationCheck, UiText.WebSocketStatusNotification, cardX + 115, chkY, 145, 24);
        _hUuEvasionCheck = CreateCheckBox(IdUuEvasionCheck, UiText.UuEvasion, cardX + 268, chkY, 125, 24);

        y = _rcCard3.Bottom + 14;

        // ---------- 4. 操作栏 ----------
        _hBtnServiceMenu = CreateButton(IdBtnServiceMenu, "服务 ▾", cardX, y, 84, 32, isAccent: false);

        var saveBtnW = 120;
        var closeBtnW = 68;
        var saveBtnX = cardX + cardW - saveBtnW;
        var closeBtnX = saveBtnX - closeBtnW - 8;

        _hBtnClose = CreateButton(IdBtnClose, "关闭", closeBtnX, y, closeBtnW, 32, isAccent: false);
        _hBtnSaveAndReconnect = CreateButton(IdBtnSaveAndReconnect, UiText.SaveAndReconnect, saveBtnX, y, saveBtnW, 32, isAccent: true);

        y += 42;

        // ---------- 5. 底部紧凑状态卡片（无 WebSocket，大幅收缩留白） ----------
        var cardStatusTop = y;
        var cardStatusH = 46;
        _rcCardStatus = new NativeMethods.RECT { Left = cardX, Top = cardStatusTop, Right = cardX + cardW, Bottom = cardStatusTop + cardStatusH };

        var statRow = cardStatusTop + 14;
        _hSessionLabel = CreateLabel("", cardX + 34, statRow, 210, 18, _fontCaption, isCardInternal: true);
        _hServiceLabel = CreateLabel("", cardX + 264, statRow, 220, 18, _fontCaption, isCardInternal: true);
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
            x + 8, y + 4, w - 16, h - 8,
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
            x, y + 2, w, 150,
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

    private IntPtr CreateCertModeComboBox(int id, int x, int y, int w, int h)
    {
        var hwnd = NativeMethods.CreateWindowEx(
            0,
            "COMBOBOX",
            string.Empty,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.CBS_DROPDOWNLIST | NativeMethods.CBS_HASSTRINGS,
            x, y + 2, w, 160,
            _hwnd,
            (IntPtr)id,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_fontBody, 1);
        NativeMethods.SendMessageString(hwnd, NativeMethods.CB_ADDSTRING, 0, "使用系统证书验证");
        NativeMethods.SendMessageString(hwnd, NativeMethods.CB_ADDSTRING, 0, "指定证书 SHA-256 指纹");
        NativeMethods.SendMessageString(hwnd, NativeMethods.CB_ADDSTRING, 0, "信任所有证书");
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

            // 密码框占位水印
            _isEditingPassword = false;
            if (data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPassword))
            {
                NativeMethods.SendMessageString(_hPasswordBox, NativeMethods.EM_SETCUEBANNER, 0, UiText.SavedPasswordPlaceholder);
                NativeMethods.SetWindowText(_hPasswordBox, "");
                NativeMethods.SetWindowText(_hBtnChangePassword, "更换");
            }
            else
            {
                NativeMethods.SendMessageString(_hPasswordBox, NativeMethods.EM_SETCUEBANNER, 0, "");
                NativeMethods.SetWindowText(_hPasswordBox, "");
                NativeMethods.SetWindowText(_hBtnChangePassword, "输入");
            }

            NativeMethods.SetWindowText(_hHashRoundsBox, data.HashRounds.ToString());
            NativeMethods.SetWindowText(_hSaltBox, data.Salt ?? "");

            // 本地剪贴板上限单位自适应换算
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

            // 证书校验模式下拉框映射
            if (data.TrustAllCertificates)
            {
                NativeMethods.SendMessage(_hCertModeCombo, NativeMethods.CB_SETCURSEL, 2, 0); // 信任所有证书
            }
            else if (!string.IsNullOrWhiteSpace(data.ServerCertificateThumbprint))
            {
                NativeMethods.SendMessage(_hCertModeCombo, NativeMethods.CB_SETCURSEL, 1, 0); // 指定证书 SHA-256 指纹
            }
            else
            {
                NativeMethods.SendMessage(_hCertModeCombo, NativeMethods.CB_SETCURSEL, 0, 0); // 使用系统证书验证
            }

            NativeMethods.SetWindowText(_hCertThumbprintBox, data.ServerCertificateThumbprint ?? "");

            SetCheck(_hCipherCheck, data.CipherEnabled);
            SetCheck(_hSavePasswordCheck, data.SavePassword);
            SetCheck(_hStartupCheck, data.RelaunchOnBoot);
            SetCheck(_hStatusNotificationCheck, data.WebsocketStatusNotification);
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

        // 剪贴板上限单位回算为标准字节
        var limitStr = GetControlText(_hLocalLimitBox);
        if (long.TryParse(limitStr, out var limitVal) && limitVal > 0)
        {
            var curSel = (int)NativeMethods.SendMessage(_hUnitCombo, NativeMethods.CB_GETCURSEL, 0, 0);
            var multiplier = curSel == 1 ? (1024L * 1024L) : 1024L;
            data.LocalMaxClipboardBytes = limitVal * multiplier;
        }

        // 证书校验三模式映射
        var certMode = (int)NativeMethods.SendMessage(_hCertModeCombo, NativeMethods.CB_GETCURSEL, 0, 0);
        switch (certMode)
        {
            case 2: // 信任所有证书
                data.TrustAllCertificates = true;
                data.ServerCertificateThumbprint = string.Empty;
                break;
            case 1: // 指定证书指纹
                data.TrustAllCertificates = false;
                data.ServerCertificateThumbprint = GetControlText(_hCertThumbprintBox);
                break;
            default: // 使用系统证书
                data.TrustAllCertificates = false;
                data.ServerCertificateThumbprint = string.Empty;
                break;
        }

        data.CipherEnabled = IsChecked(_hCipherCheck);
        data.SavePassword = IsChecked(_hSavePasswordCheck);
        data.WebsocketStatusNotification = IsChecked(_hStatusNotificationCheck);
        data.UuEvasionEnabled = IsChecked(_hUuEvasionCheck);

        // 仅在用户主动更换了新密码时才保存新密码
        if (_isEditingPassword)
        {
            var typedPassword = GetControlText(_hPasswordBox);
            if (!string.IsNullOrWhiteSpace(typedPassword))
            {
                data.SavedPassword = typedPassword;
            }
        }
        _app.SaveSettings();
    }

    private void UpdateFieldsEnableState()
    {
        var cipherOn = IsChecked(_hCipherCheck);
        NativeMethods.EnableWindow(_hHashRoundsBox, cipherOn);
        NativeMethods.EnableWindow(_hSaltBox, cipherOn);

        // 证书指纹框：仅在选择“指定证书 SHA-256 指纹”(模式 1)时才启用并展示
        var certMode = (int)NativeMethods.SendMessage(_hCertModeCombo, NativeMethods.CB_GETCURSEL, 0, 0);
        var fingerprintActive = certMode == 1;

        NativeMethods.EnableWindow(_hCertThumbprintBox, fingerprintActive);
        NativeMethods.ShowWindow(_hCertThumbprintLabel, fingerprintActive ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        NativeMethods.ShowWindow(_hCertThumbprintBox, fingerprintActive ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);

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

            case NativeMethods.WM_SETTINGCHANGE:
                OnSettingChange();
                return 0;

            case NativeMethods.WM_CLOSE:
                LoadFromSettings();
                Hide();
                return 0;

            case NativeMethods.WM_COMMAND:
                var ctrlId = (int)(wParam & 0xFFFF);
                var code = (int)((wParam >> 16) & 0xFFFF);
                OnCommand(ctrlId, code);
                return 0;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private unsafe void OnPaint()
    {
        var hdc = NativeMethods.BeginPaint(_hwnd, out var ps);
        try
        {
            NativeMethods.FillRect(hdc, in ps.rcPaint, _hBrushBg);

            // 绘制三大卡片与底部紧凑状态卡片
            DrawCard(hdc, in _rcCard1);
            DrawCard(hdc, in _rcCard2);
            DrawCard(hdc, in _rcCard3);
            DrawCard(hdc, in _rcCardStatus);

            // 绘制大标题专属 Logo 图标（20x20 高精度矢量图标，原比例镂空）
            NativeMethods.DrawIconEx(hdc, _rcCard1.Left + 16, _rcCard1.Top + 9, AppIcons.IconConnHandle, 20, 20, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
            NativeMethods.DrawIconEx(hdc, _rcCard2.Left + 16, _rcCard2.Top + 9, AppIcons.IconSecHandle, 20, 20, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
            NativeMethods.DrawIconEx(hdc, _rcCard3.Left + 16, _rcCard3.Top + 9, AppIcons.IconOptHandle, 20, 20, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);

            // 绘制所有可见文本输入框的圆角矩形外框（Fluent 8px 圆角）
            foreach (var (hEdit, rc) in _editBoxes)
            {
                var certMode = (int)NativeMethods.SendMessage(_hCertModeCombo, NativeMethods.CB_GETCURSEL, 0, 0);
                if (hEdit == _hCertThumbprintBox && certMode != 1)
                {
                    continue;
                }

                var isEnabled = NativeMethods.IsWindowEnabled(hEdit);
                var brush = isEnabled ? _hBrushEditBg : _hBrushEditDisabledBg;
                var pen = isEnabled ? _hPenEditBorder : _hPenEditDisabledBorder;

                var prevBrush = NativeMethods.SelectObject(hdc, brush);
                var prevPen = NativeMethods.SelectObject(hdc, pen);
                NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 8, 8);
                NativeMethods.SelectObject(hdc, prevBrush);
                NativeMethods.SelectObject(hdc, prevPen);
            }

            // 绘制底部状态栏双指示灯
            DrawStatusIndicatorDots(hdc);
        }
        finally
        {
            NativeMethods.EndPaint(_hwnd, in ps);
        }
    }

    private void DrawStatusIndicatorDots(IntPtr hdc)
    {
        var loggedIn = _app.IsLoggedIn;
        var running = _app.ServiceRunning;
        var paused = _app.IsPaused;

        var dot1Col = loggedIn ? 0x00107C41U : 0x00808080U;
        var dot2Col = running ? (paused ? 0x00013BD8U : 0x00107C41U) : 0x00808080U;

        var rowCenterY = _rcCardStatus.Top + 23;

        // 行 1 左列指示圆点（会话状态）
        var dot1Left = _rcCardStatus.Left + 20;
        var hBrush1 = NativeMethods.CreateSolidBrush(dot1Col);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush1);
        NativeMethods.SelectObject(hdc, NativeMethods.GetStockObject(15));
        NativeMethods.Ellipse(hdc, dot1Left, rowCenterY - 3, dot1Left + 7, rowCenterY + 4);

        // 行 1 右列指示圆点（服务状态）
        var dot2Left = _rcCardStatus.Left + 250;
        var hBrush2 = NativeMethods.CreateSolidBrush(dot2Col);
        NativeMethods.SelectObject(hdc, hBrush2);
        NativeMethods.Ellipse(hdc, dot2Left, rowCenterY - 3, dot2Left + 7, rowCenterY + 4);

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

        if (_allEditControls.Contains(hwndCtrl))
        {
            var isEnabled = NativeMethods.IsWindowEnabled(hwndCtrl);
            var editBg = isEnabled
                ? (_isDarkMode ? 0x00222222U : 0x00FFFFFFU)
                : (_isDarkMode ? 0x00262626U : 0x00F4F4F4U);
            var editText = isEnabled
                ? (_isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU)
                : (_isDarkMode ? 0x00777777U : 0x008E8E8EU);

            NativeMethods.SetBkColor(hdc, editBg);
            NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
            NativeMethods.SetTextColor(hdc, editText);
            return isEnabled ? _hBrushEditBg : _hBrushEditDisabledBg;
        }

        if (_cardLabels.Contains(hwndCtrl))
        {
            var cardBg = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
            var textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;

            NativeMethods.SetBkColor(hdc, cardBg);
            NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
            NativeMethods.SetTextColor(hdc, textCol);
            return _hBrushCard;
        }

        var outerBg = _isDarkMode ? 0x00202020U : 0x00F3F3F3U;
        var outerTextCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
        NativeMethods.SetBkColor(hdc, outerBg);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
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
            : (_isDarkMode ? 0x00262626U : 0x00F4F4F4U);
        var editText = isEnabled
            ? (_isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU)
            : (_isDarkMode ? 0x00777777U : 0x008E8E8EU);

        NativeMethods.SetBkColor(hdc, editBg);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
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

        // 高级黑白主题主按钮（浅色下深黑底白字，深色下白底黑字）
        uint btnCol;
        uint textCol;

        if (isDisabled)
        {
            btnCol = _isDarkMode ? 0x00333333U : 0x00E5E5E5U;
            textCol = _isDarkMode ? 0x00777777U : 0x008E8E8EU;
        }
        else if (_isDarkMode)
        {
            // 深色模式：白底黑字反差
            btnCol = isSelected ? 0x00CCCCCCU : 0x00FFFFFFU;
            textCol = 0x001A1A1AU;
        }
        else
        {
            // 浅色模式：深黑底白字高级质感
            btnCol = isSelected ? 0x00333333U : 0x001A1A1AU;
            textCol = 0x00FFFFFFU;
        }

        var hBrush = NativeMethods.CreateSolidBrush(btnCol);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
        var prevPen = NativeMethods.SelectObject(hdc, _hPenBorder);

        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 8, 8);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);

        var btnText = UiText.SaveAndReconnect;
        NativeMethods.SetBkColor(hdc, btnCol);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, textCol);

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

    private void OnCommand(int ctrlId, int code)
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

            case IdBtnClose:
                LoadFromSettings();
                Hide();
                break;

            case IdBtnAccount:
                if (_app.IsLoggedIn)
                {
                    _ = LogoutAsync();
                }
                else
                {
                    _ = SaveAndReconnectAsync();
                }
                break;

            case IdBtnChangePassword:
                TogglePasswordEditing();
                break;

            case IdBtnServiceMenu:
                ShowServiceMenu();
                break;

            case IdCipherCheck:
                UpdateFieldsEnableState();
                break;

            case IdCertModeCombo:
                if (code == (int)NativeMethods.CBN_SELCHANGE)
                {
                    OnCertModeChanged();
                }
                break;
        }
    }

    private void TogglePasswordEditing()
    {
        _isEditingPassword = !_isEditingPassword;
        if (_isEditingPassword)
        {
            NativeMethods.SendMessageString(_hPasswordBox, NativeMethods.EM_SETCUEBANNER, 0, "");
            NativeMethods.SetWindowText(_hPasswordBox, "");
            NativeMethods.SetWindowText(_hBtnChangePassword, "取消");
        }
        else
        {
            LoadFromSettings();
        }
    }

    private void OnCertModeChanged()
    {
        var certMode = (int)NativeMethods.SendMessage(_hCertModeCombo, NativeMethods.CB_GETCURSEL, 0, 0);

        // 选择“信任所有证书”时弹出警告
        if (certMode == 2)
        {
            var result = NativeMethods.MessageBox(
                _hwnd,
                UiText.TrustCertConfirmDialogBody,
                UiText.TrustCertWarningTitle,
                NativeMethods.MB_YESNO | NativeMethods.MB_ICONWARNING | NativeMethods.MB_DEFBUTTON2);

            if (result != NativeMethods.IDYES)
            {
                NativeMethods.SendMessage(_hCertModeCombo, NativeMethods.CB_SETCURSEL, 0, 0);
            }
        }

        UpdateFieldsEnableState();
    }

    private void ShowServiceMenu()
    {
        var hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var loggedIn = _app.IsLoggedIn;
            var running = _app.ServiceRunning;
            var paused = _app.IsPaused;

            var restartFlags = (loggedIn && running) ? NativeMethods.MF_STRING : (NativeMethods.MF_STRING | NativeMethods.MF_GRAYED);
            NativeMethods.AppendMenu(hMenu, restartFlags, IdmMenuRestart, "重启服务");

            var pauseText = paused ? "恢复同步" : "暂停同步";
            var pauseFlags = running ? NativeMethods.MF_STRING : (NativeMethods.MF_STRING | NativeMethods.MF_GRAYED);
            NativeMethods.AppendMenu(hMenu, pauseFlags, IdmMenuTogglePause, pauseText);

            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_SEPARATOR, 0, string.Empty);
            NativeMethods.AppendMenu(hMenu, NativeMethods.MF_STRING, IdmMenuConnectionDetails, "连接详情");

            NativeMethods.GetCursorPos(out var pt);
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
                case (uint)IdmMenuRestart:
                    _ = RestartServiceAsync();
                    break;
                case (uint)IdmMenuTogglePause:
                    _app.TogglePause();
                    break;
                case (uint)IdmMenuConnectionDetails:
                    ShowConnectionDetailsDialog();
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    private void ShowConnectionDetailsDialog()
    {
        var wsUrl = string.IsNullOrWhiteSpace(_currentWebsocketUrl) ? "无" : _currentWebsocketUrl;
        NativeClipboard.TrySetText(wsUrl);

        var detailsText = $"WebSocket 地址：\n{wsUrl}\n\n状态：{(_app.ServiceRunning ? (_app.IsPaused ? "连接正常，同步已暂停" : "连接正常，同步已就绪") : "未连接")}\n\n（地址已自动复制到系统剪贴板）";
        NativeMethods.MessageBox(_hwnd, detailsText, "连接详情", NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
    }

    private async Task SaveAndReconnectAsync()
    {
        SetBusy(true);

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
            }
            else
            {
                await _app.StopServiceAsync().ConfigureAwait(true);
                var rounds = int.TryParse(GetControlText(_hHashRoundsBox), out var r) && r > 0 ? r : ClipConfig.DefaultHashRounds;
                var data = _app.SettingsStore.Data;
                var request = new LoginRequest(
                    GetControlText(_hServerUrlBox),
                    GetControlText(_hUsernameBox),
                    hasPassword && (string.IsNullOrWhiteSpace(typedPassword) || !_isEditingPassword) ? data.SavedPassword : typedPassword,
                    rounds,
                    GetControlText(_hSaltBox),
                    data.TrustAllCertificates,
                    data.ServerCertificateThumbprint);

                await _app.LoginAsync(request, _disposeCts.Token).ConfigureAwait(true);
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
        NativeMethods.EnableWindow(_hBtnAccount, !busy);
        NativeMethods.EnableWindow(_hBtnClose, !busy);
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

        if (_fontCardTitle != IntPtr.Zero) NativeMethods.DeleteObject(_fontCardTitle);
        if (_fontBody != IntPtr.Zero) NativeMethods.DeleteObject(_fontBody);
        if (_fontCaption != IntPtr.Zero) NativeMethods.DeleteObject(_fontCaption);

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
