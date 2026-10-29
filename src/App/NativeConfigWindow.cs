using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 严格参考 textcascade-desktop.html 表单栅格排版（112px 标签列 + 紧凑控件流）的纯原生 Win32 配置窗口。
// 核心排版特性：
//   - 左侧固定 112px 标签列，控件紧密衔接，彻底消除空白荒原感
//   - 密码框支持占位掩码与「更换」按钮动态切换
//   - 剪贴板大小上限纯净无“单条内容”后缀，支持 KB/MB 换算
//   - 证书校验三模式下拉框（系统证书 / SHA-256指纹 / 信任所有证书），信任自签时安全弹窗警告
//   - 选项卡片单行收纳「开机启动」、「连接变化时通知」、「UU远程规避」
//   - 操作栏集成「服务 ▾」菜单（包含重启、暂停/恢复同步、连接详情），右侧主次按钮清晰（关闭 + 保存并重连）
//   - 纯净双行底部状态栏，带发光彩色指示灯
//   - 100% 纯原生 NativeAOT 零反射、零第三方依赖。
internal sealed class NativeConfigWindow : IDisposable
{
    private const string WindowClassName = "TextCascade_FluentConfigWndClass_v5";

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
    private IntPtr _fontTitle;
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
    private IntPtr _hPasswordHintLabel;

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
    private IntPtr _hActionFeedbackLabel;
    private IntPtr _hBtnClose;
    private IntPtr _hBtnSaveAndReconnect;

    private IntPtr _hServiceStatusLabel;
    private IntPtr _hAccountStatusLabel;
    private IntPtr _hConnectionStatusLabel;
    private IntPtr _hFeedbackLabel;

    // 静态标签分类跟踪（彻底解决背景色块问题）
    private readonly HashSet<IntPtr> _cardLabels = new();
    private readonly HashSet<IntPtr> _outerLabels = new();
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

        // 现代标准宽度 640，高度 760，完全契合 HTML 原型紧凑格局
        _hwnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            "TextCascade · 设置",
            NativeMethods.WS_OVERLAPPED | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX,
            140, 70, 640, 765,
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
        if (_hFeedbackLabel != IntPtr.Zero)
        {
            NativeMethods.SetWindowText(_hFeedbackLabel, display);
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
        var paused = _app.IsPaused;

        // 账号与连接状态
        NativeMethods.SetWindowText(_hAccountStatusLabel, loggedIn ? "账号：已登录" : "账号：未登录");
        NativeMethods.SetWindowText(_hConnectionStatusLabel, running ? (paused ? "连接：同步暂停" : "连接：已连接") : "连接：未连接");
        NativeMethods.SetWindowText(_hServiceStatusLabel, running ? (paused ? "服务运行中(已暂停)" : "服务运行中") : "服务已停止");

        // 按钮文本与可用状态
        NativeMethods.SetWindowText(_hBtnAccount, loggedIn ? "退出登录" : "登录");
        NativeMethods.EnableWindow(_hBtnAccount, !_updating);
        NativeMethods.EnableWindow(_hBtnSaveAndReconnect, !_updating);

        _currentWebsocketUrl = DisplayWebsocketUrl(data.ServerUrl);

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
            -18, 0, 0, 0, NativeMethods.FW_BOLD, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        _fontCardTitle = NativeMethods.CreateFont(
            -14, 0, 0, 0, NativeMethods.FW_SEMIBOLD, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        _fontBody = NativeMethods.CreateFont(
            -13, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        _fontCaption = NativeMethods.CreateFont(
            -12, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
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
        var cardX = 20;
        var cardW = 584; // 紧凑卡片宽度
        var labelW = 112; // 严格参考 HTML 的 112px 标签宽度
        var ctrlX = cardX + 16 + labelW + 12; // 160px
        var ctrlW = cardX + cardW - 16 - ctrlX; // 428px

        _editBoxes.Clear();
        _allEditControls.Clear();
        _cardLabels.Clear();
        _outerLabels.Clear();

        var y = 14;

        // ---------- 1. 卡片 1：连接（Section Connection） ----------
        var card1Top = y;
        var rowH = 36;
        var card1H = 30 + rowH * 4 + 8;
        _rcCard1 = new NativeMethods.RECT { Left = cardX, Top = card1Top, Right = cardX + cardW, Bottom = card1Top + card1H };

        CreateLabel("连接", cardX + 16, card1Top + 8, 200, 20, _fontCardTitle, isCardInternal: true);

        var rowY = card1Top + 30;
        // 服务器地址
        CreateLabel("服务器地址", cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hServerUrlBox = CreateRoundedEdit(IdServerUrl, ctrlX, rowY + 1, ctrlW, 28);
        rowY += rowH;

        // 用户名 + 退出登录
        CreateLabel("用户名", cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        var userEditW = ctrlW - 88;
        _hUsernameBox = CreateRoundedEdit(IdUsername, ctrlX, rowY + 1, userEditW, 28);
        _hBtnAccount = CreateButton(IdBtnAccount, "退出登录", ctrlX + userEditW + 8, rowY + 1, 80, 28, isAccent: false);
        rowY += rowH;

        // 密码 + 更换
        CreateLabel("密码", cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        var pwdEditW = ctrlW - 88;
        _hPasswordBox = CreateRoundedEdit(IdPassword, ctrlX, rowY + 1, pwdEditW, 28, isPassword: true);
        _hBtnChangePassword = CreateButton(IdBtnChangePassword, "更换", ctrlX + pwdEditW + 8, rowY + 1, 80, 28, isAccent: false);
        rowY += rowH;

        // 保存密码 + 浅灰提示说明
        _hSavePasswordCheck = CreateCheckBox(IdSavePasswordCheck, "保存密码", ctrlX, rowY + 3, 100, 22);
        _hPasswordHintLabel = CreateLabel("已保存的密码将继续使用", ctrlX + 105, rowY + 6, 300, 18, _fontCaption, isCardInternal: true);

        y = _rcCard1.Bottom + 12;

        // ---------- 2. 卡片 2：安全与限制（Section Security） ----------
        var card2Top = y;
        var card2H = 30 + rowH * 5 + 8;
        _rcCard2 = new NativeMethods.RECT { Left = cardX, Top = card2Top, Right = cardX + cardW, Bottom = card2Top + card2H };

        CreateLabel("安全与限制", cardX + 16, card2Top + 8, 200, 20, _fontCardTitle, isCardInternal: true);

        rowY = card2Top + 30;
        // 剪贴板大小上限（纯净无单条内容）
        CreateLabel("剪贴板大小上限", cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hLocalLimitBox = CreateRoundedEdit(IdLocalLimit, ctrlX, rowY + 1, 100, 28);
        _hUnitCombo = CreateComboBox(IdUnitCombo, ctrlX + 108, rowY + 1, 74, 28);
        rowY += rowH;

        // 内容加密
        CreateLabel("内容加密", cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hCipherCheck = CreateCheckBox(IdCipherCheck, "启用加密", ctrlX, rowY + 3, 140, 22);
        rowY += rowH;

        // 加密迭代次数 + 加密盐
        CreateLabel("加密迭代次数", cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hHashRoundsBox = CreateRoundedEdit(IdHashRounds, ctrlX, rowY + 1, 76, 28);
        CreateLabel("加密盐", ctrlX + 88, rowY + 5, 54, 20, _fontBody, isCardInternal: true);
        var saltW = ctrlW - 146;
        _hSaltBox = CreateRoundedEdit(IdSalt, ctrlX + 146, rowY + 1, saltW, 28);
        rowY += rowH;

        // 证书校验（三模式下拉框）
        CreateLabel("证书校验", cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hCertModeCombo = CreateCertModeComboBox(IdCertModeCombo, ctrlX, rowY + 1, 225, 28);
        rowY += rowH;

        // SHA-256 指纹（动态根据下拉框展示）
        _hCertThumbprintLabel = CreateLabel("SHA-256 指纹", cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hCertThumbprintBox = CreateRoundedEdit(IdCertThumbprint, ctrlX, rowY + 1, ctrlW, 28);

        y = _rcCard2.Bottom + 12;

        // ---------- 3. 卡片 3：选项（Section Options，单行横向排开） ----------
        var card3Top = y;
        var card3H = 30 + 32 + 8;
        _rcCard3 = new NativeMethods.RECT { Left = cardX, Top = card3Top, Right = cardX + cardW, Bottom = card3Top + card3H };

        CreateLabel("选项", cardX + 16, card3Top + 8, 200, 20, _fontCardTitle, isCardInternal: true);

        var chkY = card3Top + 32;
        _hStartupCheck = CreateCheckBox(IdStartupCheck, "开机启动", cardX + 16, chkY, 110, 24);
        _hStatusNotificationCheck = CreateCheckBox(IdStatusNotificationCheck, "连接变化时通知", cardX + 136, chkY, 160, 24);
        _hUuEvasionCheck = CreateCheckBox(IdUuEvasionCheck, "UU远程规避", cardX + 306, chkY, 160, 24);

        y = _rcCard3.Bottom + 14;

        // ---------- 4. 操作栏（Actions Bar） ----------
        _hBtnServiceMenu = CreateButton(IdBtnServiceMenu, "服务 ▾", cardX, y, 86, 32, isAccent: false);
        _hActionFeedbackLabel = CreateLabel("设置已保存", cardX + 96, y + 6, 260, 20, _fontCaption, isCardInternal: false);

        var saveBtnW = 128;
        var closeBtnW = 72;
        var saveBtnX = cardX + cardW - saveBtnW;
        var closeBtnX = saveBtnX - closeBtnW - 8;

        _hBtnClose = CreateButton(IdBtnClose, "关闭", closeBtnX, y, closeBtnW, 32, isAccent: false);
        _hBtnSaveAndReconnect = CreateButton(IdBtnSaveAndReconnect, "保存并重连", saveBtnX, y, saveBtnW, 32, isAccent: true);

        y += 44;

        // ---------- 5. 底部状态栏（Footer Statusbar） ----------
        var cardStatusTop = y;
        var cardStatusH = 68;
        _rcCardStatus = new NativeMethods.RECT { Left = cardX, Top = cardStatusTop, Right = cardX + cardW, Bottom = cardStatusTop + cardStatusH };

        var statRow1 = cardStatusTop + 10;
        var statRow2 = cardStatusTop + 36;

        _hServiceStatusLabel = CreateLabel("服务运行中", cardX + 32, statRow1, 150, 18, _fontCaption, isCardInternal: true);
        _hAccountStatusLabel = CreateLabel("账号：已登录", cardX + 195, statRow1, 150, 18, _fontCaption, isCardInternal: true);
        _hConnectionStatusLabel = CreateLabel("连接：已连接", cardX + 360, statRow1, 180, 18, _fontCaption, isCardInternal: true);

        _hFeedbackLabel = CreateLabel("同步已就绪，等待剪贴板变化", cardX + 18, statRow2, cardW - 36, 18, _fontCaption, isCardInternal: true);
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

            // 密码框掩码与状态提示
            _isEditingPassword = false;
            if (data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPassword))
            {
                NativeMethods.SetWindowText(_hPasswordBox, "••••••••");
                NativeMethods.SetWindowText(_hBtnChangePassword, "更换");
                NativeMethods.SetWindowText(_hPasswordHintLabel, "已保存的密码将继续使用");
            }
            else
            {
                NativeMethods.SetWindowText(_hPasswordBox, "");
                NativeMethods.SetWindowText(_hBtnChangePassword, "输入");
                NativeMethods.SetWindowText(_hPasswordHintLabel, "退出登录后需要重新输入");
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

        // 仅在用户主动更换了新密码时才保存新密码，避免将掩码当密码保存
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

            DrawCard(hdc, in _rcCard1);
            DrawCard(hdc, in _rcCard2);
            DrawCard(hdc, in _rcCard3);
            DrawCard(hdc, in _rcCardStatus);

            // 绘制所有可见文本输入框的圆角矩形外框
            foreach (var (hEdit, rc) in _editBoxes)
            {
                // 如果指纹输入框被隐藏，则不画外框
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
                NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 6, 6);
                NativeMethods.SelectObject(hdc, prevBrush);
                NativeMethods.SelectObject(hdc, prevPen);
            }

            DrawStatusIndicatorDots(hdc);
        }
        finally
        {
            NativeMethods.EndPaint(_hwnd, in ps);
        }
    }

    private void DrawStatusIndicatorDots(IntPtr hdc)
    {
        var running = _app.ServiceRunning;
        var paused = _app.IsPaused;

        // 浅绿正常，橙黄暂停，浅灰断开
        var dotCol = running ? (paused ? 0x00013BD8U : 0x00107C41U) : 0x00808080U;
        var dotLeft = _rcCardStatus.Left + 18;
        var dotTop = _rcCardStatus.Top + 16;

        var hBrush = NativeMethods.CreateSolidBrush(dotCol);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
        NativeMethods.SelectObject(hdc, NativeMethods.GetStockObject(15)); // NULL_PEN
        NativeMethods.Ellipse(hdc, dotLeft, dotTop, dotLeft + 7, dotTop + 7);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.DeleteObject(hBrush);
    }

    private void DrawCard(IntPtr hdc, in NativeMethods.RECT rc)
    {
        var prevBrush = NativeMethods.SelectObject(hdc, _hBrushCard);
        var prevPen = NativeMethods.SelectObject(hdc, _hPenBorder);

        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 12, 12);

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
                : (_isDarkMode ? 0x00262626U : 0x00F4F4F4U);
            var editText = isEnabled
                ? (_isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU)
                : (_isDarkMode ? 0x00777777U : 0x00878787U);
            NativeMethods.SetBkColor(hdc, editBg);
            NativeMethods.SetTextColor(hdc, editText);
            return isEnabled ? _hBrushEditBg : _hBrushEditDisabledBg;
        }

        if (_cardLabels.Contains(hwndCtrl))
        {
            var textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            if (hwndCtrl == _hPasswordHintLabel || hwndCtrl == _hFeedbackLabel)
            {
                textCol = _isDarkMode ? 0x009E9E9EU : 0x00626262U;
            }
            NativeMethods.SetTextColor(hdc, textCol);
            return _hBrushCard;
        }

        var outerTextCol = _isDarkMode ? 0x009E9E9EU : 0x00626262U;
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
            : (_isDarkMode ? 0x00777777U : 0x00878787U);
        NativeMethods.SetBkColor(hdc, editBg);
        NativeMethods.SetTextColor(hdc, editText);
        return isEnabled ? _hBrushEditBg : _hBrushEditDisabledBg;
    }

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

        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 6, 6);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);

        var btnText = "保存并重连";
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
            NativeMethods.SetWindowText(_hPasswordBox, "");
            NativeMethods.SetWindowText(_hBtnChangePassword, "取消");
            NativeMethods.SetWindowText(_hPasswordHintLabel, "保存后使用新密码");
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
                // 用户取消，回退至使用系统证书
                NativeMethods.SendMessage(_hCertModeCombo, NativeMethods.CB_SETCURSEL, 0, 0);
            }
        }

        UpdateFieldsEnableState();
    }

    // 弹出「服务 ▾」原生上下文菜单（包含重启、暂停/恢复、连接详情）
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
        SetStatus("配置已保存，正在重新连接…");
        NativeMethods.SetWindowText(_hActionFeedbackLabel, "正在保存…");

        try
        {
            SaveFormSettings();

            var typedPassword = GetControlText(_hPasswordBox);
            var hasPassword = !string.IsNullOrWhiteSpace(typedPassword)
                || (_app.SettingsStore.Data.SavePassword && !string.IsNullOrWhiteSpace(_app.SettingsStore.Data.SavedPassword));

            if (!hasPassword && !_app.IsLoggedIn)
            {
                SetStatus(UiText.RequiredLoginFields);
                NativeMethods.SetWindowText(_hActionFeedbackLabel, "请填写完整凭据");
                return;
            }

            if (_app.IsLoggedIn && !hasPassword)
            {
                SetStatus(UiText.SaveSuccessful);
                NativeMethods.SetWindowText(_hActionFeedbackLabel, "设置已保存");
            }
            else
            {
                await _app.StopServiceAsync().ConfigureAwait(true);
                var rounds = int.TryParse(GetControlText(_hHashRoundsBox), out var r) && r > 0 ? r : ClipConfig.DefaultHashRounds;
                var data = _app.SettingsStore.Data;
                var request = new LoginRequest(
                    GetControlText(_hServerUrlBox),
                    GetControlText(_hUsernameBox),
                    hasPassword && (string.IsNullOrWhiteSpace(typedPassword) || typedPassword == "••••••••") ? data.SavedPassword : typedPassword,
                    rounds,
                    GetControlText(_hSaltBox),
                    data.TrustAllCertificates,
                    data.ServerCertificateThumbprint);

                await _app.LoginAsync(request, _disposeCts.Token).ConfigureAwait(true);
                SetStatus("连接成功，剪贴板同步已就绪");
                NativeMethods.SetWindowText(_hActionFeedbackLabel, "设置已保存");
            }

            LoadFromSettings();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            SetStatus(UiText.SaveFailed(error.Message));
            NativeMethods.SetWindowText(_hActionFeedbackLabel, "保存失败");
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
            SetStatus("已退出登录，同步已停止");
            NativeMethods.SetWindowText(_hActionFeedbackLabel, "已退出登录");
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
            SetStatus("服务已重启，连接正常");
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

        if (_fontTitle != IntPtr.Zero) NativeMethods.DeleteObject(_fontTitle);
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
