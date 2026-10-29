using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 拥有 Windows 11 现代沉浸式大标题栏（48px Custom Titlebar）+ HTML 紧凑栅格排版的纯原生 Win32 配置窗口。
// 核心升级：
//   - 沉浸式 48px 大标题栏：彻底移除了 Windows 默认局促的 31px 窄白条，左侧嵌入全新黑白 App Logo（24x24）与纯正 TextCascade 标题（删除无意义设置二字）；
//   - 右上角集成自绘 Windows 11 现代矢量最小化（细横线）与关闭按钮（细叉），按客户区宽度绝对贴边对齐；
//   - 卡片大标题 Logo 采用自绘与 DrawText(DT_VCENTER) 共享水平垂直中心线，100% 消除错位；
//   - 卡片 2（安全与限制）采用高度自适应（当未启用 SHA-256 时收缩为 4 行，彻底消灭底部多余空白）；
//   - 所有开关复选框改为自绘纯黑白主题（浅色纯黑底白勾，暗色纯白底黑勾，告别刺眼系统蓝）；
//   - 服务按钮与证书/单位下拉框全面 Fluent 化（自绘圆角、纤细向下箭头、自绘下拉高亮项）；
//   - 弹窗全面 Fluent 化（FluentDialog 模态对话框，彻底淘汰古老 MessageBox）；
//   - 全应用高级黑白主题（浅色深黑底白字，深色纯白底黑字），极简克制；
//   - 100% 纯原生 NativeAOT 零反射、零第三方依赖，秒级冷启。
internal sealed class NativeConfigWindow : IDisposable
{
    private const string WindowClassName = "TextCascade_FluentConfigWndClass_v10";

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

    private const int IdCaptionMin = 301;
    private const int IdCaptionClose = 302;

    // 「服务 ▾」菜单项 ID
    private const nuint IdmMenuRestart = 101;
    private const nuint IdmMenuTogglePause = 102;
    private const nuint IdmMenuConnectionDetails = 103;

    private static readonly ConcurrentDictionary<IntPtr, NativeConfigWindow> Instances = new();
    private static readonly ConcurrentDictionary<IntPtr, (NativeConfigWindow Window, IntPtr OldWndProc, bool IsClose)> CaptionBtnInstances = new();
    private static readonly NativeMethods.WndProc StaticWndProc = WindowProcedure;
    private static readonly NativeMethods.WndProc StaticCaptionBtnProc = CaptionButtonWindowProcedure;
    private static readonly object RegisterLock = new();
    private static bool _classRegistered;

    private readonly TrayHost _app;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly IntPtr _hwnd;

    private bool _isMinHovered;
    private bool _isCloseHovered;
    private int _currentUnitIndex = 0; // 0: KB, 1: MB
    private int _currentCertMode = 0; // 0: 使用系统证书验证, 1: 指定证书 SHA-256 指纹, 2: 信任所有证书

    // 现代字阶
    private IntPtr _fontTitle;
    private IntPtr _fontCardTitle;
    private IntPtr _fontBody;
    private IntPtr _fontCaption;

    // 控件句柄
    private IntPtr _hCaptionBtnMin;
    private IntPtr _hCaptionBtnClose;

    private IntPtr _hServerUrlBox;
    private IntPtr _hUsernameBox;
    private IntPtr _hBtnAccount;
    private IntPtr _hPasswordBox;
    private IntPtr _hBtnChangePassword;
    private IntPtr _hSavePasswordCheck;

    private IntPtr _hLocalLimitBox;
    private IntPtr _hUnitCombo;
    private IntPtr _hCipherCheck;
    private IntPtr _hHashRoundsLabel;
    private IntPtr _hHashRoundsBox;
    private IntPtr _hSaltLabel;
    private IntPtr _hSaltBox;
    private IntPtr _hCertModeLabel;
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
    private readonly HashSet<int> _checkedBoxes = new();

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

    // 卡片与标题栏矩形坐标
    private NativeMethods.RECT _rcTitlebar;
    private NativeMethods.RECT _btnMinRect;
    private NativeMethods.RECT _btnCloseRect;
    private NativeMethods.RECT _rcCard1;
    private NativeMethods.RECT _rcCard2;
    private NativeMethods.RECT _rcCard3;
    private NativeMethods.RECT _rcCardStatus;

    public NativeConfigWindow(TrayHost app)
    {
        _app = app;
        _isDarkMode = DetectIsDarkMode();
        UpdateThemeBrushes();

        EnsureWindowClassRegistered();

        try
        {
            NativeMethods.SetPreferredAppMode(_isDarkMode ? 2 : 1);
        }
        catch
        {
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            "TextCascade",
            NativeMethods.WS_OVERLAPPED | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_THICKFRAME | NativeMethods.WS_CLIPCHILDREN,
            150, 70, 540, 700,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            Instances[_hwnd] = this;
            InitializeFonts();
            ApplyDwmAttributes();
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
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

        // 底部状态展示
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

        if (_fontTitle != IntPtr.Zero) NativeMethods.DeleteObject(_fontTitle);
        if (_fontCardTitle != IntPtr.Zero) NativeMethods.DeleteObject(_fontCardTitle);
        if (_fontBody != IntPtr.Zero) NativeMethods.DeleteObject(_fontBody);
        if (_fontCaption != IntPtr.Zero) NativeMethods.DeleteObject(_fontCaption);

        // 标题栏大标题：14pt，半粗
        var hTitle = -PtToPixelHeight(14, dpi);
        _fontTitle = NativeMethods.CreateFont(
            hTitle, 0, 0, 0, NativeMethods.FW_SEMIBOLD, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyDemibold);

        // 卡片标题：11.5pt，绑定 MiSans Demibold 真粗体
        var hCardTitle = -PtToPixelHeight(11.5, dpi);
        _fontCardTitle = NativeMethods.CreateFont(
            hCardTitle, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyDemibold);

        // 控件正文：9.5pt
        var hBody = -PtToPixelHeight(9.5, dpi);
        _fontBody = NativeMethods.CreateFont(
            hBody, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        // 说明文字：8.5pt
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
        NativeMethods.GetClientRect(_hwnd, out var rcClient);
        var winW = rcClient.Right - rcClient.Left;
        if (winW <= 0) winW = 540;

        var cardX = 14;
        var cardW = winW - 28;
        var labelW = 100;
        var ctrlX = cardX + 16 + labelW + 8;
        var ctrlW = cardX + cardW - 14 - ctrlX;

        _editBoxes.Clear();
        _allEditControls.Clear();
        _cardLabels.Clear();

        // ---------- 1. 沉浸式 48px 大标题栏按键 ----------
        _rcTitlebar = new NativeMethods.RECT { Left = 0, Top = 0, Right = winW, Bottom = 48 };
        _btnMinRect = new NativeMethods.RECT { Left = winW - 92, Top = 0, Right = winW - 46, Bottom = 48 };
        _btnCloseRect = new NativeMethods.RECT { Left = winW - 46, Top = 0, Right = winW, Bottom = 48 };

        _hCaptionBtnMin = CreateButton(IdCaptionMin, string.Empty, _btnMinRect.Left, 0, 46, 48, isAccent: false, isCaptionBtn: true);
        _hCaptionBtnClose = CreateButton(IdCaptionClose, string.Empty, _btnCloseRect.Left, 0, 46, 48, isAccent: false, isCaptionBtn: true);
        SubclassCaptionButton(_hCaptionBtnMin, isClose: false);
        SubclassCaptionButton(_hCaptionBtnClose, isClose: true);

        var y = 56;

        // ---------- 2. 卡片 1：连接与认证 ----------
        var card1Top = y;
        var rowH = 36;
        var card1H = 36 + rowH * 3 + 28 + 8;
        _rcCard1 = new NativeMethods.RECT { Left = cardX, Top = card1Top, Right = cardX + cardW, Bottom = card1Top + card1H };

        var rowY = card1Top + 36;
        CreateLabel(UiText.ServerUrl, cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hServerUrlBox = CreateRoundedEdit(IdServerUrl, ctrlX, rowY + 1, ctrlW, 28);
        rowY += rowH;

        CreateLabel(UiText.Username, cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        var userEditW = ctrlW - 84;
        _hUsernameBox = CreateRoundedEdit(IdUsername, ctrlX, rowY + 1, userEditW, 28);
        _hBtnAccount = CreateButton(IdBtnAccount, UiText.Logout, ctrlX + userEditW + 8, rowY + 1, 76, 28, isAccent: false);
        rowY += rowH;

        CreateLabel(UiText.Password, cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        var pwdEditW = ctrlW - 84;
        _hPasswordBox = CreateRoundedEdit(IdPassword, ctrlX, rowY + 1, pwdEditW, 28, isPassword: true);
        _hBtnChangePassword = CreateButton(IdBtnChangePassword, "更换", ctrlX + pwdEditW + 8, rowY + 1, 76, 28, isAccent: false);
        rowY += rowH;

        _hSavePasswordCheck = CreateCheckBox(IdSavePasswordCheck, UiText.SavePassword, ctrlX, rowY + 3, 160, 24);

        y = _rcCard1.Bottom + 12;

        // ---------- 3. 卡片 2：安全与限制 ----------
        var card2Top = y;
        var card2H = 36 + rowH * 4 + 8; // 默认自适应为 4 行
        _rcCard2 = new NativeMethods.RECT { Left = cardX, Top = card2Top, Right = cardX + cardW, Bottom = card2Top + card2H };

        rowY = card2Top + 36;
        CreateLabel(UiText.LocalMaxClipboardBytes, cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hLocalLimitBox = CreateRoundedEdit(IdLocalLimit, ctrlX, rowY + 1, 95, 28);
        _hUnitCombo = CreateButton(IdUnitCombo, string.Empty, ctrlX + 103, rowY + 1, 72, 28, isAccent: false);
        rowY += rowH;

        CreateLabel("内容加密", cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hCipherCheck = CreateCheckBox(IdCipherCheck, UiText.EnableEncryption, ctrlX, rowY + 3, 140, 24);
        rowY += rowH;

        _hHashRoundsLabel = CreateLabel(UiText.HashRounds, cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hHashRoundsBox = CreateRoundedEdit(IdHashRounds, ctrlX, rowY + 1, 65, 28);
        _hSaltLabel = CreateLabel(UiText.EncryptionSalt, ctrlX + 73, rowY + 5, 48, 20, _fontBody, isCardInternal: true);
        var saltW = ctrlW - 129;
        _hSaltBox = CreateRoundedEdit(IdSalt, ctrlX + 129, rowY + 1, saltW, 28);
        rowY += rowH;

        _hCertModeLabel = CreateLabel("证书校验", cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hCertModeCombo = CreateButton(IdCertModeCombo, string.Empty, ctrlX, rowY + 1, ctrlW, 28, isAccent: false);
        rowY += rowH;

        _hCertThumbprintLabel = CreateLabel(UiText.ServerCertificateThumbprint, cardX + 16, rowY + 5, labelW, 20, _fontBody, isCardInternal: true);
        _hCertThumbprintBox = CreateRoundedEdit(IdCertThumbprint, ctrlX, rowY + 1, ctrlW, 28);

        y = _rcCard2.Bottom + 12;

        // ---------- 4. 卡片 3：选项（单行横排） ----------
        var card3Top = y;
        var card3H = 36 + 32 + 8;
        _rcCard3 = new NativeMethods.RECT { Left = cardX, Top = card3Top, Right = cardX + cardW, Bottom = card3Top + card3H };

        var chkY = card3Top + 36;
        _hStartupCheck = CreateCheckBox(IdStartupCheck, UiText.StartWithWindows, cardX + 14, chkY, 95, 24);
        _hStatusNotificationCheck = CreateCheckBox(IdStatusNotificationCheck, UiText.WebSocketStatusNotification, cardX + 115, chkY, 145, 24);
        _hUuEvasionCheck = CreateCheckBox(IdUuEvasionCheck, UiText.UuEvasion, cardX + 268, chkY, 125, 24);

        y = _rcCard3.Bottom + 14;

        // ---------- 5. 操作栏 ----------
        _hBtnServiceMenu = CreateButton(IdBtnServiceMenu, "服务", cardX, y, 84, 32, isAccent: false);

        var saveBtnW = 120;
        var closeBtnW = 68;
        var saveBtnX = cardX + cardW - saveBtnW;
        var closeBtnX = saveBtnX - closeBtnW - 8;

        _hBtnClose = CreateButton(IdBtnClose, "关闭", closeBtnX, y, closeBtnW, 32, isAccent: false);
        _hBtnSaveAndReconnect = CreateButton(IdBtnSaveAndReconnect, UiText.SaveAndReconnect, saveBtnX, y, saveBtnW, 32, isAccent: true);

        y += 42;

        // ---------- 6. 底部紧凑状态卡片 ----------
        var cardStatusTop = y;
        var cardStatusH = 46;
        _rcCardStatus = new NativeMethods.RECT { Left = cardX, Top = cardStatusTop, Right = cardX + cardW, Bottom = cardStatusTop + cardStatusH };

        var statRow = cardStatusTop + 14;
        _hSessionLabel = CreateLabel(string.Empty, cardX + 34, statRow, 205, 18, _fontCaption, isCardInternal: true);
        _hServiceLabel = CreateLabel(string.Empty, cardX + 250, statRow, 220, 18, _fontCaption, isCardInternal: true);
    }

    private void RelayoutControls()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.GetClientRect(_hwnd, out var rcClient);
        var winW = rcClient.Right - rcClient.Left;
        if (winW <= 0) winW = 540;

        var cardX = 14;
        var cardW = winW - 28;
        var labelW = 100;
        var ctrlX = cardX + 16 + labelW + 8;
        var ctrlW = cardX + cardW - 14 - ctrlX;
        var rowH = 36;

        // 重新停靠右上角大标题栏按钮
        _btnMinRect = new NativeMethods.RECT { Left = winW - 92, Top = 0, Right = winW - 46, Bottom = 48 };
        _btnCloseRect = new NativeMethods.RECT { Left = winW - 46, Top = 0, Right = winW, Bottom = 48 };
        NativeMethods.SetWindowPos(_hCaptionBtnMin, IntPtr.Zero, _btnMinRect.Left, 0, 46, 48, NativeMethods.SWP_NOZORDER);
        NativeMethods.SetWindowPos(_hCaptionBtnClose, IntPtr.Zero, _btnCloseRect.Left, 0, 46, 48, NativeMethods.SWP_NOZORDER);

        var fingerprintActive = _currentCertMode == 1;

        // 卡片 2 高度自适应：未选指纹时 4 行 (188px)，选指纹时 5 行 (224px)
        var card2Rows = fingerprintActive ? 5 : 4;
        var card2H = 36 + rowH * card2Rows + 8;
        _rcCard2 = new NativeMethods.RECT { Left = cardX, Top = _rcCard1.Bottom + 12, Right = cardX + cardW, Bottom = _rcCard1.Bottom + 12 + card2H };

        NativeMethods.ShowWindow(_hCertThumbprintLabel, fingerprintActive ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        NativeMethods.ShowWindow(_hCertThumbprintBox, fingerprintActive ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);

        if (fingerprintActive)
        {
            var fpRowY = _rcCard2.Top + 36 + rowH * 4;
            NativeMethods.SetWindowPos(_hCertThumbprintLabel, IntPtr.Zero, cardX + 16, fpRowY + 5, labelW, 20, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.SetWindowPos(_hCertThumbprintBox, IntPtr.Zero, ctrlX + 8, fpRowY + 5, ctrlW - 16, 20, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.EnableWindow(_hCertThumbprintBox, true);
            NativeMethods.InvalidateRect(_hCertThumbprintBox, IntPtr.Zero, true);

            // 更新编辑框列表中的坐标
            for (int i = 0; i < _editBoxes.Count; i++)
            {
                if (_editBoxes[i].Hwnd == _hCertThumbprintBox)
                {
                    _editBoxes[i] = (_hCertThumbprintBox, new NativeMethods.RECT { Left = ctrlX, Top = fpRowY + 1, Right = ctrlX + ctrlW, Bottom = fpRowY + 29 });
                    break;
                }
            }
        }
        else
        {
            NativeMethods.EnableWindow(_hCertThumbprintBox, false);
        }

        // 卡片 3 紧随卡片 2 移动
        var card3Top = _rcCard2.Bottom + 12;
        var card3H = 36 + 32 + 8;
        _rcCard3 = new NativeMethods.RECT { Left = cardX, Top = card3Top, Right = cardX + cardW, Bottom = card3Top + card3H };

        var chkY = card3Top + 36;
        NativeMethods.SetWindowPos(_hStartupCheck, IntPtr.Zero, cardX + 14, chkY, 95, 24, NativeMethods.SWP_NOZORDER);
        NativeMethods.SetWindowPos(_hStatusNotificationCheck, IntPtr.Zero, cardX + 115, chkY, 145, 24, NativeMethods.SWP_NOZORDER);
        NativeMethods.SetWindowPos(_hUuEvasionCheck, IntPtr.Zero, cardX + 268, chkY, 125, 24, NativeMethods.SWP_NOZORDER);

        // 操作栏按钮
        var opY = _rcCard3.Bottom + 16;
        NativeMethods.SetWindowPos(_hBtnServiceMenu, IntPtr.Zero, cardX, opY, 84, 32, NativeMethods.SWP_NOZORDER);

        var saveBtnW = 120;
        var closeBtnW = 68;
        var saveBtnX = cardX + cardW - saveBtnW;
        var closeBtnX = saveBtnX - closeBtnW - 8;

        NativeMethods.SetWindowPos(_hBtnClose, IntPtr.Zero, closeBtnX, opY, closeBtnW, 32, NativeMethods.SWP_NOZORDER);
        NativeMethods.SetWindowPos(_hBtnSaveAndReconnect, IntPtr.Zero, saveBtnX, opY, saveBtnW, 32, NativeMethods.SWP_NOZORDER);

        // 底部状态卡片（按钮下方留白 16px，严格物理不重叠）
        var statTop = opY + 32 + 16;
        var statH = 46;
        _rcCardStatus = new NativeMethods.RECT { Left = cardX, Top = statTop, Right = cardX + cardW, Bottom = statTop + statH };

        var statRow = statTop + 14;
        NativeMethods.SetWindowPos(_hSessionLabel, IntPtr.Zero, cardX + 34, statRow, 205, 18, NativeMethods.SWP_NOZORDER);
        NativeMethods.SetWindowPos(_hServiceLabel, IntPtr.Zero, cardX + 250, statRow, 220, 18, NativeMethods.SWP_NOZORDER);

        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
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

    private void ShowUnitMenu()
    {
        var hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(hMenu, _currentUnitIndex == 0 ? (NativeMethods.MF_STRING | NativeMethods.MF_CHECKED) : NativeMethods.MF_STRING, 1, "KB");
            NativeMethods.AppendMenu(hMenu, _currentUnitIndex == 1 ? (NativeMethods.MF_STRING | NativeMethods.MF_CHECKED) : NativeMethods.MF_STRING, 2, "MB");

            NativeMethods.GetWindowRect(_hUnitCombo, out var btnRc);
            NativeMethods.SetForegroundWindow(_hwnd);
            var cmd = NativeMethods.TrackPopupMenuEx(
                hMenu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_LEFTALIGN | NativeMethods.TPM_TOPALIGN,
                btnRc.Left, btnRc.Bottom + 2,
                _hwnd, IntPtr.Zero);

            if (cmd == 1)
            {
                _currentUnitIndex = 0;
                NativeMethods.InvalidateRect(_hUnitCombo, IntPtr.Zero, true);
            }
            else if (cmd == 2)
            {
                _currentUnitIndex = 1;
                NativeMethods.InvalidateRect(_hUnitCombo, IntPtr.Zero, true);
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    private void ShowCertModeMenu()
    {
        var hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(hMenu, _currentCertMode == 0 ? (NativeMethods.MF_STRING | NativeMethods.MF_CHECKED) : NativeMethods.MF_STRING, 1, "使用系统证书验证");
            NativeMethods.AppendMenu(hMenu, _currentCertMode == 1 ? (NativeMethods.MF_STRING | NativeMethods.MF_CHECKED) : NativeMethods.MF_STRING, 2, "指定证书 SHA-256 指纹");
            NativeMethods.AppendMenu(hMenu, _currentCertMode == 2 ? (NativeMethods.MF_STRING | NativeMethods.MF_CHECKED) : NativeMethods.MF_STRING, 3, "信任所有证书");

            NativeMethods.GetWindowRect(_hCertModeCombo, out var btnRc);
            NativeMethods.SetForegroundWindow(_hwnd);
            var cmd = NativeMethods.TrackPopupMenuEx(
                hMenu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_LEFTALIGN | NativeMethods.TPM_TOPALIGN,
                btnRc.Left, btnRc.Bottom + 2,
                _hwnd, IntPtr.Zero);

            if (cmd >= 1 && cmd <= 3)
            {
                var targetMode = (int)cmd - 1;
                if (targetMode == 2)
                {
                    var confirmed = FluentDialog.ShowConfirm(
                        _hwnd,
                        UiText.TrustCertWarningTitle,
                        UiText.TrustCertConfirmDialogBody,
                        _isDarkMode);
                    if (!confirmed)
                    {
                        return;
                    }
                }

                _currentCertMode = targetMode;
                NativeMethods.InvalidateRect(_hCertModeCombo, IntPtr.Zero, true);
                UpdateFieldsEnableState();
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(hMenu);
        }
    }

    private void SubclassCaptionButton(IntPtr hBtn, bool isClose)
    {
        var oldProc = NativeMethods.SetWindowLongPtr(hBtn, NativeMethods.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(StaticCaptionBtnProc));
        CaptionBtnInstances[hBtn] = (this, oldProc, isClose);
    }

    private static nint CaptionButtonWindowProcedure(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
    {
        if (CaptionBtnInstances.TryGetValue(hWnd, out var item))
        {
            return item.Window.HandleCaptionBtnMessage(hWnd, msg, wParam, lParam, item.OldWndProc, item.IsClose);
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private nint HandleCaptionBtnMessage(IntPtr hWnd, uint msg, nuint wParam, nint lParam, IntPtr oldWndProc, bool isClose)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEMOVE:
                var wasHovered = isClose ? _isCloseHovered : _isMinHovered;
                if (!wasHovered)
                {
                    if (isClose) _isCloseHovered = true;
                    else _isMinHovered = true;

                    var tme = new NativeMethods.TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                        dwFlags = NativeMethods.TME_LEAVE,
                        hwndTrack = hWnd,
                        dwHoverTime = 0
                    };
                    NativeMethods.TrackMouseEvent(ref tme);
                    NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                }
                break;

            case NativeMethods.WM_MOUSELEAVE:
                if (isClose) _isCloseHovered = false;
                else _isMinHovered = false;
                NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                break;

            case 0x0082: // WM_NCDESTROY
                CaptionBtnInstances.TryRemove(hWnd, out _);
                break;
        }

        return NativeMethods.CallWindowProc(oldWndProc, hWnd, msg, wParam, lParam);
    }

    private IntPtr CreateCheckBox(int id, string text, int x, int y, int w, int h)
    {
        var hwnd = NativeMethods.CreateWindowEx(
            0,
            "BUTTON",
            text,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.BS_OWNERDRAW,
            x, y, w, h,
            _hwnd,
            (IntPtr)id,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_fontBody, 1);
        return hwnd;
    }

    private IntPtr CreateButton(int id, string text, int x, int y, int w, int h, bool isAccent, bool isCaptionBtn = false)
    {
        var style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.BS_OWNERDRAW;

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

    private void SetCheck(int id, bool isChecked)
    {
        if (isChecked)
        {
            _checkedBoxes.Add(id);
        }
        else
        {
            _checkedBoxes.Remove(id);
        }
        var hCtrl = GetHwndById(id);
        if (hCtrl != IntPtr.Zero)
        {
            NativeMethods.InvalidateRect(hCtrl, IntPtr.Zero, true);
        }
    }

    private bool IsChecked(int id) => _checkedBoxes.Contains(id);

    private IntPtr GetHwndById(int id) => id switch
    {
        IdSavePasswordCheck => _hSavePasswordCheck,
        IdCipherCheck => _hCipherCheck,
        IdStartupCheck => _hStartupCheck,
        IdStatusNotificationCheck => _hStatusNotificationCheck,
        IdUuEvasionCheck => _hUuEvasionCheck,
        _ => IntPtr.Zero
    };

    private void LoadFromSettings()
    {
        _updating = true;
        try
        {
            var data = _app.SettingsStore.Data;
            NativeMethods.SetWindowText(_hServerUrlBox, data.ServerUrl ?? string.Empty);
            NativeMethods.SetWindowText(_hUsernameBox, data.Username ?? string.Empty);

            _isEditingPassword = false;
            if (data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPassword))
            {
                NativeMethods.SendMessageString(_hPasswordBox, NativeMethods.EM_SETCUEBANNER, 0, UiText.SavedPasswordPlaceholder);
                NativeMethods.SetWindowText(_hPasswordBox, string.Empty);
                NativeMethods.SetWindowText(_hBtnChangePassword, "更换");
            }
            else
            {
                NativeMethods.SendMessageString(_hPasswordBox, NativeMethods.EM_SETCUEBANNER, 0, string.Empty);
                NativeMethods.SetWindowText(_hPasswordBox, string.Empty);
                NativeMethods.SetWindowText(_hBtnChangePassword, "输入");
            }

            NativeMethods.SetWindowText(_hHashRoundsBox, data.HashRounds.ToString());
            NativeMethods.SetWindowText(_hSaltBox, data.Salt ?? string.Empty);

            var bytes = data.LocalMaxClipboardBytes;
            if (bytes > 0 && bytes % (1024 * 1024) == 0 && bytes >= (1024 * 1024))
            {
                NativeMethods.SetWindowText(_hLocalLimitBox, (bytes / (1024 * 1024)).ToString());
                _currentUnitIndex = 1;
            }
            else
            {
                var kb = Math.Max(1, bytes / 1024);
                NativeMethods.SetWindowText(_hLocalLimitBox, kb.ToString());
                _currentUnitIndex = 0;
            }

            if (data.TrustAllCertificates)
            {
                _currentCertMode = 2;
            }
            else if (!string.IsNullOrWhiteSpace(data.ServerCertificateThumbprint))
            {
                _currentCertMode = 1;
            }
            else
            {
                _currentCertMode = 0;
            }

            NativeMethods.SetWindowText(_hCertThumbprintBox, data.ServerCertificateThumbprint ?? string.Empty);

            SetCheck(IdCipherCheck, data.CipherEnabled);
            SetCheck(IdSavePasswordCheck, data.SavePassword);
            SetCheck(IdStartupCheck, data.RelaunchOnBoot);
            SetCheck(IdStatusNotificationCheck, data.WebsocketStatusNotification);
            SetCheck(IdUuEvasionCheck, data.UuEvasionEnabled);

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

        var limitStr = GetControlText(_hLocalLimitBox);
        if (long.TryParse(limitStr, out var limitVal) && limitVal > 0)
        {
            var multiplier = _currentUnitIndex == 1 ? (1024L * 1024L) : 1024L;
            data.LocalMaxClipboardBytes = limitVal * multiplier;
        }

        switch (_currentCertMode)
        {
            case 2:
                data.TrustAllCertificates = true;
                data.ServerCertificateThumbprint = string.Empty;
                break;
            case 1:
                data.TrustAllCertificates = false;
                data.ServerCertificateThumbprint = GetControlText(_hCertThumbprintBox);
                break;
            default:
                data.TrustAllCertificates = false;
                data.ServerCertificateThumbprint = string.Empty;
                break;
        }

        data.CipherEnabled = IsChecked(IdCipherCheck);
        data.SavePassword = IsChecked(IdSavePasswordCheck);
        data.WebsocketStatusNotification = IsChecked(IdStatusNotificationCheck);
        data.UuEvasionEnabled = IsChecked(IdUuEvasionCheck);

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
        var cipherOn = IsChecked(IdCipherCheck);
        NativeMethods.EnableWindow(_hHashRoundsBox, cipherOn);
        NativeMethods.EnableWindow(_hSaltBox, cipherOn);

        RelayoutControls();
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
            case 0x0083: // WM_NCCALCSIZE
                if (wParam != 0)
                {
                    // 消除系统 31px 默认窄白条非客户区，使 48px 大标题栏完全沉浸式展示
                    return 0;
                }
                return 0;

            case 0x0084: // WM_NCHITTEST
                var screenX = (short)(lParam & 0xFFFF);
                var screenY = (short)((lParam >> 16) & 0xFFFF);
                var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
                NativeMethods.ScreenToClient(_hwnd, ref pt);

                // 顶部 48px 大标题栏拖动支持
                if (pt.Y >= 0 && pt.Y <= 48)
                {
                    // 避开右上角最小化与关闭按钮
                    if (pt.X >= _btnMinRect.Left && pt.X <= _btnMinRect.Right && pt.Y >= _btnMinRect.Top && pt.Y <= _btnMinRect.Bottom)
                    {
                        return 1; // HTCLIENT
                    }
                    if (pt.X >= _btnCloseRect.Left && pt.X <= _btnCloseRect.Right && pt.Y >= _btnCloseRect.Top && pt.Y <= _btnCloseRect.Bottom)
                    {
                        return 1; // HTCLIENT
                    }
                    return 2; // HTCAPTION (允许按住自由拖拽移动窗口)
                }
                return 1; // HTCLIENT (防止 WS_THICKFRAME 边框被误判为调整大小)

            case 0x00A3: // WM_NCLBUTTONDBLCLK
                return 0; // 阻止双击最大化

            case NativeMethods.WM_PAINT:
                OnPaint();
                return 0;

            case NativeMethods.WM_CTLCOLORSTATIC:
            case NativeMethods.WM_CTLCOLORBTN:
                return OnCtlColorStatic(wParam, lParam);

            case NativeMethods.WM_CTLCOLOREDIT:
                return OnCtlColorEdit(wParam, lParam);

            case NativeMethods.WM_CTLCOLORLISTBOX:
                var hdcList = (IntPtr)wParam;
                var listBg = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
                var listText = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
                NativeMethods.SetBkColor(hdcList, listBg);
                NativeMethods.SetBkMode(hdcList, NativeMethods.OPAQUE);
                NativeMethods.SetTextColor(hdcList, listText);
                return _hBrushCard;

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

            // 1. 绘制 48px 沉浸式大标题栏（左侧 24x24 黑白 App Logo + TextCascade 纯标题，彻底删除“设置”二字）
            NativeMethods.DrawIconEx(hdc, 16, 12, AppIcons.AppHandle24, 24, 24, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);

            var titleText = "TextCascade";
            var textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;

            NativeMethods.SetBkColor(hdc, _isDarkMode ? 0x00202020U : 0x00F3F3F3U);
            NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);

            var prevFont = NativeMethods.SelectObject(hdc, _fontTitle);
            NativeMethods.SetTextColor(hdc, textCol);
            var titleRc = new NativeMethods.RECT { Left = 48, Top = 0, Right = 240, Bottom = 48 };
            NativeMethods.DrawText(hdc, titleText, titleText.Length, ref titleRc, NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
            NativeMethods.SelectObject(hdc, prevFont);

            // 2. 绘制三大卡片与底部紧凑状态卡片
            DrawCard(hdc, in _rcCard1);
            DrawCard(hdc, in _rcCard2);
            DrawCard(hdc, in _rcCard3);
            DrawCard(hdc, in _rcCardStatus);

            // 3. 绘制三大卡片大标题 Logo 与标题文本（严格同轴垂直居中对齐，消除错位）
            DrawCardHeader(hdc, _rcCard1, UiText.Connection, AppIcons.IconConnHandle);
            DrawCardHeader(hdc, _rcCard2, UiText.SecurityAndLimits, AppIcons.IconSecHandle);
            DrawCardHeader(hdc, _rcCard3, UiText.Options, AppIcons.IconOptHandle);

            // 4. 绘制输入框圆角矩形外框
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

            // 5. 绘制底部双状态指示圆点
            DrawStatusIndicatorDots(hdc);
        }
        finally
        {
            NativeMethods.EndPaint(_hwnd, in ps);
        }
    }

    private void DrawCardHeader(IntPtr hdc, in NativeMethods.RECT rcCard, string text, IntPtr hIcon)
    {
        var cardTop = rcCard.Top;
        var centerY = cardTop + 18; // Header 区域高 36px，几何中线位于 cardTop + 18

        // 1. 20x20 矢量高清图标垂直几何居中（centerY - 10）
        var iconX = rcCard.Left + 16;
        var iconY = centerY - 10;
        NativeMethods.DrawIconEx(hdc, iconX, iconY, hIcon, 20, 20, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);

        // 2. 标题文字垂直几何居中
        var cardCol = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
        var textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
        NativeMethods.SetBkColor(hdc, cardCol);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, textCol);

        var prevFont = NativeMethods.SelectObject(hdc, _fontCardTitle);
        var textRc = new NativeMethods.RECT
        {
            Left = iconX + 20 + 8, // 图标右侧 8px
            Top = cardTop,
            Right = rcCard.Right - 16,
            Bottom = cardTop + 36
        };
        NativeMethods.DrawText(hdc, text, text.Length, ref textRc, NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        NativeMethods.SelectObject(hdc, prevFont);
    }

    private void DrawStatusIndicatorDots(IntPtr hdc)
    {
        var loggedIn = _app.IsLoggedIn;
        var running = _app.ServiceRunning;
        var paused = _app.IsPaused;

        var dot1Col = loggedIn ? 0x00107C41U : 0x00808080U;
        var dot2Col = running ? (paused ? 0x00013BD8U : 0x00107C41U) : 0x00808080U;

        var rowCenterY = _rcCardStatus.Top + 23;

        // 会话指示圆点
        var dot1Left = _rcCardStatus.Left + 20;
        var hBrush1 = NativeMethods.CreateSolidBrush(dot1Col);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush1);
        NativeMethods.SelectObject(hdc, NativeMethods.GetStockObject(15));
        NativeMethods.Ellipse(hdc, dot1Left, rowCenterY - 3, dot1Left + 7, rowCenterY + 4);

        // 服务指示圆点
        var dot2Left = _rcCardStatus.Left + 236;
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

        // 1. 标题栏按钮（最小化 / 关闭 - 现代矢量细线绘制）
        if (pDis->CtlID == IdCaptionMin || pDis->CtlID == IdCaptionClose)
        {
            DrawCaptionButton(pDis);
            return;
        }

        // 3. 所有复选框（纯正黑白 Fluent 主题 CheckBox）
        if (pDis->CtlID == IdSavePasswordCheck ||
            pDis->CtlID == IdCipherCheck ||
            pDis->CtlID == IdStartupCheck ||
            pDis->CtlID == IdStatusNotificationCheck ||
            pDis->CtlID == IdUuEvasionCheck)
        {
            DrawFluentCheckBox(pDis);
            return;
        }

        // 4. 所有下拉选择框（单位、证书校验、服务，100% 统一为「服务」同款 Fluent 极简按钮风格）
        if (pDis->CtlID == IdUnitCombo)
        {
            DrawDropdownButton(pDis, _currentUnitIndex == 1 ? "MB" : "KB");
            return;
        }
        if (pDis->CtlID == IdCertModeCombo)
        {
            var certText = _currentCertMode switch
            {
                1 => "指定证书 SHA-256 指纹",
                2 => "信任所有证书",
                _ => "使用系统证书验证"
            };
            DrawDropdownButton(pDis, certText);
            return;
        }
        if (pDis->CtlID == IdBtnServiceMenu)
        {
            DrawDropdownButton(pDis, "服务");
            return;
        }

        // 5. 保存并重连（黑白主题 Accent 主按钮）
        if (pDis->CtlID == IdBtnSaveAndReconnect)
        {
            DrawPrimarySaveButton(pDis);
            return;
        }

        // 6. 其他标准按钮（注销、更换、关闭）
        DrawSecondaryButton(pDis);
    }

    private unsafe void DrawCaptionButton(NativeMethods.DRAWITEMSTRUCT* pDis)
    {
        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isSelected = (pDis->itemState & NativeMethods.ODS_SELECTED) != 0;
        var isClose = pDis->CtlID == IdCaptionClose;

        uint capBg;
        uint capFg;

        if (isClose)
        {
            if (isSelected)
            {
                capBg = 0x001C2BC4U;
                capFg = 0x00FFFFFFU;
            }
            else if (_isCloseHovered)
            {
                capBg = 0x002311E8U;
                capFg = 0x00FFFFFFU;
            }
            else
            {
                capBg = _isDarkMode ? 0x00202020U : 0x00F3F3F3U;
                capFg = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            }
        }
        else
        {
            if (isSelected)
            {
                capBg = _isDarkMode ? 0x00404040U : 0x00CCCCCCU;
                capFg = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            }
            else if (_isMinHovered)
            {
                capBg = _isDarkMode ? 0x00333333U : 0x00E5E5E5U;
                capFg = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            }
            else
            {
                capBg = _isDarkMode ? 0x00202020U : 0x00F3F3F3U;
                capFg = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            }
        }

        var hCapBrush = NativeMethods.CreateSolidBrush(capBg);
        NativeMethods.FillRect(hdc, in rc, hCapBrush);
        NativeMethods.DeleteObject(hCapBrush);

        var centerX = (rc.Left + rc.Right) / 2;
        var centerY = (rc.Top + rc.Bottom) / 2;

        var hPen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, capFg);
        var prevPen = NativeMethods.SelectObject(hdc, hPen);

        if (isClose)
        {
            // 交叉 10px 现代细线
            NativeMethods.MoveToEx(hdc, centerX - 5, centerY - 5, IntPtr.Zero);
            NativeMethods.LineTo(hdc, centerX + 5, centerY + 5);
            NativeMethods.MoveToEx(hdc, centerX - 5, centerY + 5, IntPtr.Zero);
            NativeMethods.LineTo(hdc, centerX + 5, centerY - 5);
        }
        else
        {
            // 水平 10px 现代极简细线
            NativeMethods.MoveToEx(hdc, centerX - 5, centerY, IntPtr.Zero);
            NativeMethods.LineTo(hdc, centerX + 5, centerY);
        }

        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hPen);
    }

    private unsafe void DrawFluentCheckBox(NativeMethods.DRAWITEMSTRUCT* pDis)
    {
        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isChecked = IsChecked((int)pDis->CtlID);

        // 填充控件背板底色
        NativeMethods.FillRect(hdc, in rc, _hBrushCard);

        var boxSize = 18;
        var boxLeft = rc.Left;
        var boxTop = rc.Top + (rc.Bottom - rc.Top - boxSize) / 2;
        var boxRight = boxLeft + boxSize;
        var boxBottom = boxTop + boxSize;

        if (isChecked)
        {
            // 纯黑白主题选中状态：浅色纯黑底白勾，暗色纯白底黑勾
            var boxCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            var checkCol = _isDarkMode ? 0x001A1A1AU : 0x00FFFFFFU;

            var hBrush = NativeMethods.CreateSolidBrush(boxCol);
            var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
            var hPen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, boxCol);
            var prevPen = NativeMethods.SelectObject(hdc, hPen);

            NativeMethods.RoundRect(hdc, boxLeft, boxTop, boxRight, boxBottom, 4, 4);

            NativeMethods.SelectObject(hdc, prevBrush);
            NativeMethods.SelectObject(hdc, prevPen);
            NativeMethods.DeleteObject(hBrush);
            NativeMethods.DeleteObject(hPen);

            // 绘制纯粹利落的对勾 ✓
            var hCheckPen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 2, checkCol);
            var prevCheckPen = NativeMethods.SelectObject(hdc, hCheckPen);

            NativeMethods.MoveToEx(hdc, boxLeft + 4, boxTop + 9, IntPtr.Zero);
            NativeMethods.LineTo(hdc, boxLeft + 7, boxTop + 13);
            NativeMethods.LineTo(hdc, boxLeft + 14, boxTop + 5);

            NativeMethods.SelectObject(hdc, prevCheckPen);
            NativeMethods.DeleteObject(hCheckPen);
        }
        else
        {
            // 未选中状态：白色/深色背景，灰色边框
            var boxBg = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
            var borderCol = 0x008A8A8AU;

            var hBrush = NativeMethods.CreateSolidBrush(boxBg);
            var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
            var hPen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, borderCol);
            var prevPen = NativeMethods.SelectObject(hdc, hPen);

            NativeMethods.RoundRect(hdc, boxLeft, boxTop, boxRight, boxBottom, 4, 4);

            NativeMethods.SelectObject(hdc, prevBrush);
            NativeMethods.SelectObject(hdc, prevPen);
            NativeMethods.DeleteObject(hBrush);
            NativeMethods.DeleteObject(hPen);
        }

        // 绘制复选框文字
        var text = GetControlText(pDis->hwndItem);
        var textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
        var cardCol = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;

        NativeMethods.SetBkColor(hdc, cardCol);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, textCol);

        var prevFont = NativeMethods.SelectObject(hdc, _fontBody);
        var textRc = new NativeMethods.RECT
        {
            Left = boxRight + 8,
            Top = rc.Top,
            Right = rc.Right,
            Bottom = rc.Bottom
        };
        NativeMethods.DrawText(hdc, text, text.Length, ref textRc, NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        NativeMethods.SelectObject(hdc, prevFont);
    }

    private unsafe void DrawDropdownButton(NativeMethods.DRAWITEMSTRUCT* pDis, string text)
    {
        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isSelected = (pDis->itemState & NativeMethods.ODS_SELECTED) != 0;

        uint btnCol = isSelected
            ? (_isDarkMode ? 0x00404040U : 0x00E5E5E5U)
            : (_isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU);
        uint textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;

        var hBrush = NativeMethods.CreateSolidBrush(btnCol);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
        var prevPen = NativeMethods.SelectObject(hdc, _hPenEditBorder);

        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 8, 8);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);

        NativeMethods.SetBkColor(hdc, btnCol);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, textCol);

        var prevFont = NativeMethods.SelectObject(hdc, _fontBody);
        var drawRc = new NativeMethods.RECT { Left = rc.Left + 10, Top = rc.Top, Right = rc.Right - 22, Bottom = rc.Bottom };
        NativeMethods.DrawText(hdc, text, text.Length, ref drawRc, NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        NativeMethods.SelectObject(hdc, prevFont);

        // 向下纤细折线小箭头 ⌵ (与“服务”按钮 100% 同款风格)
        var arrowCol = _isDarkMode ? 0x00CCCCCCU : 0x00666666U;
        var arrowPen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, arrowCol);
        var prevArrowPen = NativeMethods.SelectObject(hdc, arrowPen);
        var midX = rc.Right - 13;
        var midY = (rc.Top + rc.Bottom) / 2;

        NativeMethods.MoveToEx(hdc, midX - 4, midY - 2, IntPtr.Zero);
        NativeMethods.LineTo(hdc, midX, midY + 2);
        NativeMethods.LineTo(hdc, midX + 4, midY - 2);

        NativeMethods.SelectObject(hdc, prevArrowPen);
        NativeMethods.DeleteObject(arrowPen);
    }

    private unsafe void DrawPrimarySaveButton(NativeMethods.DRAWITEMSTRUCT* pDis)
    {
        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isSelected = (pDis->itemState & NativeMethods.ODS_SELECTED) != 0;
        var isDisabled = (pDis->itemState & NativeMethods.ODS_DISABLED) != 0;

        uint btnCol;
        uint textCol;

        if (isDisabled)
        {
            btnCol = _isDarkMode ? 0x00333333U : 0x00E5E5E5U;
            textCol = _isDarkMode ? 0x00777777U : 0x008E8E8EU;
        }
        else if (_isDarkMode)
        {
            btnCol = isSelected ? 0x00CCCCCCU : 0x00FFFFFFU;
            textCol = 0x001A1A1AU;
        }
        else
        {
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

    private unsafe void DrawSecondaryButton(NativeMethods.DRAWITEMSTRUCT* pDis)
    {
        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isSelected = (pDis->itemState & NativeMethods.ODS_SELECTED) != 0;
        var isDisabled = (pDis->itemState & NativeMethods.ODS_DISABLED) != 0;

        uint btnCol = isDisabled
            ? (_isDarkMode ? 0x00262626U : 0x00F4F4F4U)
            : (isSelected
                ? (_isDarkMode ? 0x00404040U : 0x00E5E5E5U)
                : (_isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU));
        uint textCol = isDisabled
            ? (_isDarkMode ? 0x00777777U : 0x008E8E8EU)
            : (_isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU);

        var hBrush = NativeMethods.CreateSolidBrush(btnCol);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
        var prevPen = NativeMethods.SelectObject(hdc, _hPenBorder);

        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 8, 8);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);

        var btnText = GetControlText(pDis->hwndItem);
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
            case IdCaptionMin:
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWMINIMIZED);
                break;

            case IdCaptionClose:
            case IdBtnClose:
                LoadFromSettings();
                Hide();
                break;

            case IdBtnSaveAndReconnect:
                _ = SaveAndReconnectAsync();
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

            case (int)IdmMenuRestart:
                _ = RestartServiceAsync();
                break;

            case (int)IdmMenuTogglePause:
                _app.TogglePause();
                break;

            case (int)IdmMenuConnectionDetails:
                ShowConnectionDetailsDialog();
                break;

            case IdSavePasswordCheck:
            case IdStartupCheck:
            case IdStatusNotificationCheck:
            case IdUuEvasionCheck:
                SetCheck(ctrlId, !IsChecked(ctrlId));
                break;

            case IdCipherCheck:
                SetCheck(ctrlId, !IsChecked(ctrlId));
                UpdateFieldsEnableState();
                break;

            case IdUnitCombo:
                ShowUnitMenu();
                break;

            case IdCertModeCombo:
                ShowCertModeMenu();
                break;
        }
    }

    private void TogglePasswordEditing()
    {
        _isEditingPassword = !_isEditingPassword;
        if (_isEditingPassword)
        {
            NativeMethods.SendMessageString(_hPasswordBox, NativeMethods.EM_SETCUEBANNER, 0, string.Empty);
            NativeMethods.SetWindowText(_hPasswordBox, string.Empty);
            NativeMethods.SetWindowText(_hBtnChangePassword, "取消");
        }
        else
        {
            LoadFromSettings();
        }
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
        FluentDialog.ShowInfo(_hwnd, "连接详情", detailsText, _isDarkMode);
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
