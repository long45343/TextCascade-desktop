using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 严格对齐 Windows 11 Fluent Design（系统设置规范）的纯原生 Win32 配置窗口。
// 核心特性：
//   - 单列通栏圆角大卡片（Single-Column Fluent Cards）
//   - 规范设置条目化（Setting Rows）：左侧宽标题+副文本，右侧紧凑输入框
//   - 条目间浅灰分割线，彻底消除文字截断与空洞拉伸
//   - 动态字阶系统（Segoe UI Variable / Segoe UI 平滑抗锯齿）
//   - 彻底根除背景色块断层，完美支持 Windows 11 深浅色自动切换
//   - 100% 纯原生 NativeAOT 零反射、零第三方依赖。
internal sealed class NativeConfigWindow : IDisposable
{
    private const string WindowClassName = "TextCascade_FluentConfigWndClass_v2";

    // 控件命令 ID 定义
    private const int IdServerUrl = 201;
    private const int IdUsername = 202;
    private const int IdPassword = 203;
    private const int IdHashRounds = 204;
    private const int IdSalt = 205;
    private const int IdLocalLimit = 206;
    private const int IdCertThumbprint = 207;

    private const int IdCipherCheck = 211;
    private const int IdSavePasswordCheck = 212;
    private const int IdStartupCheck = 213;
    private const int IdStatusNotificationCheck = 214;
    private const int IdTrustCertCheck = 215;
    private const int IdUuEvasionCheck = 216;

    private const int IdBtnLogin = 221;
    private const int IdBtnSave = 222;
    private const int IdBtnLogout = 223;
    private const int IdBtnRestart = 224;

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
    private IntPtr _hPasswordBox;
    private IntPtr _hHashRoundsBox;
    private IntPtr _hSaltBox;
    private IntPtr _hLocalLimitBox;
    private IntPtr _hCertThumbprintBox;

    private IntPtr _hCipherCheck;
    private IntPtr _hSavePasswordCheck;
    private IntPtr _hStartupCheck;
    private IntPtr _hStatusNotificationCheck;
    private IntPtr _hTrustCertCheck;
    private IntPtr _hUuEvasionCheck;

    private IntPtr _hBtnLogin;
    private IntPtr _hBtnSave;
    private IntPtr _hBtnLogout;
    private IntPtr _hBtnRestart;

    private IntPtr _hStatusLabel;
    private IntPtr _hSessionLabel;
    private IntPtr _hServiceLabel;
    private IntPtr _hWebsocketLabel;

    // 静态标签分类跟踪（彻底解决背景色块问题）
    private readonly HashSet<IntPtr> _cardLabels = new();
    private readonly HashSet<IntPtr> _outerLabels = new();

    // 界面状态与画刷缓存
    private bool _isDarkMode;
    private bool _updating;
    private bool _trustCertWarningShown;
    private bool _disposed;

    // GDI 资源
    private IntPtr _hBrushBg;
    private IntPtr _hBrushCard;
    private IntPtr _hPenBorder;
    private IntPtr _hPenDivider;

    // 卡片矩形坐标
    private NativeMethods.RECT _rcCard1;
    private NativeMethods.RECT _rcCard2;
    private NativeMethods.RECT _rcCard3;
    private NativeMethods.RECT _rcCardStatus;

    // 分割线 Y 坐标列表
    private readonly List<int> _dividers = new();

    public NativeConfigWindow(TrayHost app)
    {
        _app = app;
        _isDarkMode = DetectIsDarkMode();

        EnsureWindowClassRegistered();
        InitializeFonts();
        UpdateThemeBrushes();

        // 现代舒展尺寸：宽 736，高 910，完全容纳所有条目与底部状态，留足呼吸间距
        _hwnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            "TextCascade",
            NativeMethods.WS_OVERLAPPED | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX,
            120, 60, 736, 910,
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

        var wsText = DisplayWebsocketUrl(data.ServerUrl);
        NativeMethods.SetWindowText(_hWebsocketLabel, $"{UiText.WebSocket}: {wsText}");

        var isUnpinnedCert = data.TrustAllCertificates && string.IsNullOrWhiteSpace(data.ServerCertificateThumbprint);
        var serviceText = running
            ? (isUnpinnedCert ? UiText.RunningUnpinnedCertWarning : UiText.Running)
            : UiText.Stopped;
        NativeMethods.SetWindowText(_hServiceLabel, $"{UiText.Service}: {serviceText}");

        NativeMethods.EnableWindow(_hBtnLogin, !_updating);
        NativeMethods.EnableWindow(_hBtnSave, !_updating);
        NativeMethods.EnableWindow(_hBtnLogout, loggedIn && !_updating);
        NativeMethods.EnableWindow(_hBtnRestart, loggedIn && !_updating);
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

        // 窗口大标题：18pt 粗体
        _fontTitle = NativeMethods.CreateFont(
            -24, 0, 0, 0, NativeMethods.FW_BOLD, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        // 卡片分组标题：11.5pt 半粗体
        _fontCardTitle = NativeMethods.CreateFont(
            -15, 0, 0, 0, NativeMethods.FW_SEMIBOLD, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        // 正文/输入框/按钮：9.5pt 常规
        _fontBody = NativeMethods.CreateFont(
            -13, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        // 辅助说明/状态：8.5pt
        _fontCaption = NativeMethods.CreateFont(
            -11, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
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
        if (_hBrushBg != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_hBrushBg);
        }
        if (_hBrushCard != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_hBrushCard);
        }
        if (_hPenBorder != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_hPenBorder);
        }
        if (_hPenDivider != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_hPenDivider);
        }

        // 浅色模式: 纯净灰底 #F3F3F3, 卡片纯白 #FFFFFF, 边框 #E5E5E5, 分割线 #F0F0F0
        // 深色模式: 优雅暗底 #202020, 卡片深灰 #2C2C2C, 边框 #383838, 分割线 #333333
        var bgCol = _isDarkMode ? 0x00202020U : 0x00F3F3F3U;
        var cardCol = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
        var borderCol = _isDarkMode ? 0x00383838U : 0x00E5E5E5U;
        var dividerCol = _isDarkMode ? 0x00333333U : 0x00F0F0F0U;

        _hBrushBg = NativeMethods.CreateSolidBrush(bgCol);
        _hBrushCard = NativeMethods.CreateSolidBrush(cardCol);
        _hPenBorder = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, borderCol);
        _hPenDivider = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, dividerCol);
    }

    private void CreateChildControls()
    {
        var cardX = 24;
        var cardW = 672;
        var editW = 280; // 紧凑优雅的输入框宽度，彻底告别拉面式空洞拉长
        var editX = cardX + cardW - editW - 20;

        _dividers.Clear();

        // ---------- 1. 顶部 Header ----------
        var y = 18;
        var hTitle = CreateLabel("TextCascade", cardX, y, 160, 28, _fontTitle, isCardInternal: false);
        var hSub = CreateLabel("v2.5.1.0 • Windows 11 Fluent", cardX + 165, y + 9, 300, 18, _fontCaption, isCardInternal: false);
        y += 44;

        // ---------- 2. 卡片 1：连接与认证（3 项） ----------
        var card1Top = y;
        var card1H = 34 + 44 * 3 + 8;
        _rcCard1 = new NativeMethods.RECT { Left = cardX, Top = card1Top, Right = cardX + cardW, Bottom = card1Top + card1H };

        CreateLabel(UiText.Connection, cardX + 20, card1Top + 10, 300, 20, _fontCardTitle, isCardInternal: true);

        var rowY = card1Top + 36;
        CreateSettingRow(cardX + 20, rowY, 320, UiText.ServerUrl, "如 https://your-host:8443");
        _hServerUrlBox = CreateEdit(IdServerUrl, editX, rowY + 6, editW, 26);
        _dividers.Add(rowY + 44);
        rowY += 44;

        CreateSettingRow(cardX + 20, rowY, 320, UiText.Username, "服务端分配的登录账号");
        _hUsernameBox = CreateEdit(IdUsername, editX, rowY + 6, editW, 26);
        _dividers.Add(rowY + 44);
        rowY += 44;

        CreateSettingRow(cardX + 20, rowY, 320, UiText.Password, "登录密码（勾选保存密码后落盘加密）");
        _hPasswordBox = CreateEdit(IdPassword, editX, rowY + 6, editW, 26, isPassword: true);

        y = _rcCard1.Bottom + 14;

        // ---------- 3. 卡片 2：安全与限制（4 项） ----------
        var card2Top = y;
        var card2H = 34 + 44 * 4 + 8;
        _rcCard2 = new NativeMethods.RECT { Left = cardX, Top = card2Top, Right = cardX + cardW, Bottom = card2Top + card2H };

        CreateLabel(UiText.SecurityAndLimits, cardX + 20, card2Top + 10, 300, 20, _fontCardTitle, isCardInternal: true);

        rowY = card2Top + 36;
        CreateSettingRow(cardX + 20, rowY, 320, UiText.HashRounds, "PBKDF2 派生密钥迭代轮数（默认 664937）");
        _hHashRoundsBox = CreateEdit(IdHashRounds, editX, rowY + 6, editW, 26);
        _dividers.Add(rowY + 44);
        rowY += 44;

        CreateSettingRow(cardX + 20, rowY, 320, UiText.EncryptionSalt, "密钥盐后缀（各端一致）");
        _hSaltBox = CreateEdit(IdSalt, editX, rowY + 6, editW, 26);
        _dividers.Add(rowY + 44);
        rowY += 44;

        // 标签宽度充足（320px），副文本清晰说明，绝不发生截断
        CreateSettingRow(cardX + 20, rowY, 320, UiText.LocalMaxClipboardBytes, "防止过大文件撑爆内存（默认 512KB）");
        _hLocalLimitBox = CreateEdit(IdLocalLimit, editX, rowY + 6, editW, 26);
        _dividers.Add(rowY + 44);
        rowY += 44;

        CreateSettingRow(cardX + 20, rowY, 320, UiText.ServerCertificateThumbprint, "自签部署时的 SHA-256 固定指纹（选填）");
        _hCertThumbprintBox = CreateEdit(IdCertThumbprint, editX, rowY + 6, editW, 26);

        y = _rcCard2.Bottom + 14;

        // ---------- 4. 卡片 3：功能选项（6 项，双列整洁排布） ----------
        var card3Top = y;
        var card3H = 34 + 32 * 3 + 12;
        _rcCard3 = new NativeMethods.RECT { Left = cardX, Top = card3Top, Right = cardX + cardW, Bottom = card3Top + card3H };

        CreateLabel(UiText.Options, cardX + 20, card3Top + 10, 300, 20, _fontCardTitle, isCardInternal: true);

        var chkCol1 = cardX + 24;
        var chkCol2 = cardX + 340;
        var chkY = card3Top + 38;

        _hCipherCheck = CreateCheckBox(IdCipherCheck, UiText.EnableEncryption, chkCol1, chkY, 290, 24);
        _hSavePasswordCheck = CreateCheckBox(IdSavePasswordCheck, UiText.SavePassword, chkCol2, chkY, 290, 24);
        chkY += 32;

        _hStartupCheck = CreateCheckBox(IdStartupCheck, UiText.StartWithWindows, chkCol1, chkY, 290, 24);
        _hStatusNotificationCheck = CreateCheckBox(IdStatusNotificationCheck, UiText.WebSocketStatusNotification, chkCol2, chkY, 290, 24);
        chkY += 32;

        _hTrustCertCheck = CreateCheckBox(IdTrustCertCheck, UiText.TrustAllCertificates, chkCol1, chkY, 290, 24);
        _hUuEvasionCheck = CreateCheckBox(IdUuEvasionCheck, UiText.UuEvasion, chkCol2, chkY, 290, 24);

        y = _rcCard3.Bottom + 16;

        // ---------- 5. 核心操作栏（ActionBar） ----------
        var btnW = 156;
        var gap = 16;
        _hBtnLogin = CreateButton(IdBtnLogin, UiText.Login, cardX, y, btnW, 36, isAccent: true);
        _hBtnSave = CreateButton(IdBtnSave, UiText.Save, cardX + btnW + gap, y, btnW, 36, isAccent: true);
        _hBtnLogout = CreateButton(IdBtnLogout, UiText.Logout, cardX + (btnW + gap) * 2, y, btnW, 36, isAccent: false);
        _hBtnRestart = CreateButton(IdBtnRestart, UiText.RestartService, cardX + (btnW + gap) * 3, y, btnW, 36, isAccent: false);

        y += 50;

        // ---------- 6. 底部状态卡片（确保绝不被窗口下沿裁切） ----------
        var cardStatusTop = y;
        var cardStatusH = 68;
        _rcCardStatus = new NativeMethods.RECT { Left = cardX, Top = cardStatusTop, Right = cardX + cardW, Bottom = cardStatusTop + cardStatusH };

        var statCol1 = cardX + 20;
        var statCol2 = cardX + 350;
        var statRow1 = cardStatusTop + 12;
        var statRow2 = cardStatusTop + 36;

        _hSessionLabel = CreateLabel("", statCol1, statRow1, 310, 18, _fontCaption, isCardInternal: true);
        _hWebsocketLabel = CreateLabel("", statCol2, statRow1, 300, 18, _fontCaption, isCardInternal: true);

        _hServiceLabel = CreateLabel("", statCol1, statRow2, 310, 18, _fontCaption, isCardInternal: true);
        _hStatusLabel = CreateLabel($"{UiText.Status}: {UiText.Idle}", statCol2, statRow2, 300, 18, _fontCaption, isCardInternal: true);
    }

    private void CreateSettingRow(int x, int y, int w, string title, string subtitle)
    {
        CreateLabel(title, x, y + 2, w, 18, _fontBody, isCardInternal: true);
        CreateLabel(subtitle, x, y + 20, w, 16, _fontCaption, isCardInternal: true, isSecondary: true);
    }

    private IntPtr CreateLabel(string text, int x, int y, int w, int h, IntPtr hFont, bool isCardInternal, bool isSecondary = false)
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

    private IntPtr CreateEdit(int id, int x, int y, int w, int h, bool isPassword = false)
    {
        var style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_BORDER | NativeMethods.WS_TABSTOP | NativeMethods.ES_AUTOHSCROLL;
        if (isPassword)
        {
            style |= NativeMethods.ES_PASSWORD;
        }

        var hwnd = NativeMethods.CreateWindowEx(
            0, // 不加 WS_EX_CLIENTEDGE，配合 Common Controls v6 拥有纯正现代扁平边框
            "EDIT",
            string.Empty,
            style,
            x, y, w, h,
            _hwnd,
            (IntPtr)id,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_fontBody, 1);
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
            NativeMethods.SetWindowText(_hPasswordBox, "");
            NativeMethods.SetWindowText(_hHashRoundsBox, data.HashRounds.ToString());
            NativeMethods.SetWindowText(_hSaltBox, data.Salt ?? "");
            NativeMethods.SetWindowText(_hLocalLimitBox, data.LocalMaxClipboardBytes.ToString());
            NativeMethods.SetWindowText(_hCertThumbprintBox, data.ServerCertificateThumbprint ?? "");

            SetCheck(_hCipherCheck, data.CipherEnabled);
            SetCheck(_hSavePasswordCheck, data.SavePassword);
            SetCheck(_hStartupCheck, data.RelaunchOnBoot);
            SetCheck(_hStatusNotificationCheck, data.WebsocketStatusNotification);
            SetCheck(_hTrustCertCheck, data.TrustAllCertificates);
            SetCheck(_hUuEvasionCheck, data.UuEvasionEnabled);

            NativeMethods.EnableWindow(_hCertThumbprintBox, data.TrustAllCertificates);
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
        if (long.TryParse(GetControlText(_hLocalLimitBox), out var limit) && limit > 0)
        {
            data.LocalMaxClipboardBytes = limit;
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
                    hbrBackground = IntPtr.Zero, // 自绘纯色底，完全避免白边闪烁
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
                OnCommand(ctrlId);
                return 0;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // 绘制 Fluent 纯色底色、单列大卡片与精致细分割线
    private unsafe void OnPaint()
    {
        var hdc = NativeMethods.BeginPaint(_hwnd, out var ps);
        try
        {
            // 1. 窗口底色铺满
            NativeMethods.FillRect(hdc, in ps.rcPaint, _hBrushBg);

            // 2. 绘制四大圆角大卡片（连接、安全、选项、状态）
            DrawCard(hdc, in _rcCard1);
            DrawCard(hdc, in _rcCard2);
            DrawCard(hdc, in _rcCard3);
            DrawCard(hdc, in _rcCardStatus);

            // 3. 绘制卡片内各条目之间的浅灰细分割线（正统 Windows 11 Fluent 灵魂细节）
            var prevPen = NativeMethods.SelectObject(hdc, _hPenDivider);
            foreach (var divY in _dividers)
            {
                var cardLeft = _rcCard1.Left + 16;
                var cardRight = _rcCard1.Right - 16;
                NativeMethods.RoundRect(hdc, cardLeft, divY, cardRight, divY + 1, 0, 0);
            }
            NativeMethods.SelectObject(hdc, prevPen);
        }
        finally
        {
            NativeMethods.EndPaint(_hwnd, in ps);
        }
    }

    private void DrawCard(IntPtr hdc, in NativeMethods.RECT rc)
    {
        var prevBrush = NativeMethods.SelectObject(hdc, _hBrushCard);
        var prevPen = NativeMethods.SelectObject(hdc, _hPenBorder);

        // Windows 11 Fluent 规范：圆角半径 8px (椭圆直径 16)
        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 16, 16);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
    }

    // 彻底解决背景色块问题：严格区隔卡片内部与卡片外部
    private nint OnCtlColorStatic(nuint wParam, nint lParam)
    {
        var hdc = (IntPtr)wParam;
        var hwndCtrl = (IntPtr)lParam;

        NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);

        if (_cardLabels.Contains(hwndCtrl))
        {
            // 卡片内部控件：严格返回卡片底色画刷，文字主白/主黑
            var textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            if (hwndCtrl == _hSessionLabel || hwndCtrl == _hWebsocketLabel
                || hwndCtrl == _hServiceLabel || hwndCtrl == _hStatusLabel)
            {
                textCol = _isDarkMode ? 0x00A0A0A0U : 0x00666666U;
            }
            NativeMethods.SetTextColor(hdc, textCol);
            return _hBrushCard;
        }

        // 卡片外部（Header 等）：严格返回外层背景画刷，彻底杜绝白色补丁方块
        var outerTextCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
        NativeMethods.SetTextColor(hdc, outerTextCol);
        return _hBrushBg;
    }

    // 自绘 Accent 蓝色圆角按钮（登录 / 保存）
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

        // Windows 11 Accent 蓝色调：正常 #0067C0，按压 #005FB8，禁用浅灰
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

        // 按钮圆角 6px (椭圆直径 12)
        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 12, 12);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);

        // 居中白色加粗文字
        var btnText = pDis->CtlID == IdBtnLogin ? UiText.Login : UiText.Save;
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
            NativeMethods.InvalidateRect(_hwnd, null, true);
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
            case IdBtnLogin:
                _ = LoginAsync();
                break;
            case IdBtnSave:
                _ = SaveAndReconnectAsync();
                break;
            case IdBtnLogout:
                _ = LogoutAsync();
                break;
            case IdBtnRestart:
                _ = RestartServiceAsync();
                break;
            case IdTrustCertCheck:
                var isTrust = IsChecked(_hTrustCertCheck);
                NativeMethods.EnableWindow(_hCertThumbprintBox, isTrust);
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

    private async Task LoginAsync()
    {
        if (!ConfirmUnpinnedCertIfNecessary())
        {
            SetStatus(UiText.OperationCancelled);
            return;
        }

        SetBusy(true);
        SetStatus(UiText.LoggingIn);
        try
        {
            SaveFormSettings();
            var rounds = int.TryParse(GetControlText(_hHashRoundsBox), out var r) && r > 0 ? r : ClipConfig.DefaultHashRounds;
            var request = new LoginRequest(
                GetControlText(_hServerUrlBox),
                GetControlText(_hUsernameBox),
                GetControlText(_hPasswordBox),
                rounds,
                GetControlText(_hSaltBox),
                IsChecked(_hTrustCertCheck),
                GetControlText(_hCertThumbprintBox));

            await _app.LoginAsync(request, _disposeCts.Token).ConfigureAwait(true);
            NativeMethods.SetWindowText(_hPasswordBox, "");
            SetStatus(UiText.LoginSuccessful);
            LoadFromSettings();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            SetStatus(UiText.LoginFailed(error.Message));
        }
        finally
        {
            SetBusy(false);
            RefreshFromState();
        }
    }

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

            if (_app.IsLoggedIn)
            {
                var typedPassword = GetControlText(_hPasswordBox);
                var hasPassword = !string.IsNullOrWhiteSpace(typedPassword)
                    || (_app.SettingsStore.Data.SavePassword && !string.IsNullOrWhiteSpace(_app.SettingsStore.Data.SavedPassword));

                if (!hasPassword)
                {
                    SetStatus(UiText.SaveSuccessful);
                }
                else
                {
                    await _app.StopServiceAsync().ConfigureAwait(true);
                    var rounds = int.TryParse(GetControlText(_hHashRoundsBox), out var r) && r > 0 ? r : ClipConfig.DefaultHashRounds;
                    var request = new LoginRequest(
                        GetControlText(_hServerUrlBox),
                        GetControlText(_hUsernameBox),
                        typedPassword,
                        rounds,
                        GetControlText(_hSaltBox),
                        IsChecked(_hTrustCertCheck),
                        GetControlText(_hCertThumbprintBox));

                    await _app.LoginAsync(request, _disposeCts.Token).ConfigureAwait(true);
                    NativeMethods.SetWindowText(_hPasswordBox, "");
                    SetStatus(UiText.LoginSuccessful);
                }
            }
            else
            {
                SetStatus(UiText.SaveSuccessful);
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
        NativeMethods.EnableWindow(_hBtnLogin, !busy);
        NativeMethods.EnableWindow(_hBtnSave, !busy);
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

        if (_fontTitle != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_fontTitle);
        }
        if (_fontCardTitle != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_fontCardTitle);
        }
        if (_fontBody != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_fontBody);
        }
        if (_fontCaption != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_fontCaption);
        }

        if (_hBrushBg != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_hBrushBg);
        }
        if (_hBrushCard != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_hBrushCard);
        }
        if (_hPenBorder != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_hPenBorder);
        }
        if (_hPenDivider != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_hPenDivider);
        }

        if (_hwnd != IntPtr.Zero)
        {
            Instances.TryRemove(_hwnd, out _);
            NativeMethods.DestroyWindow(_hwnd);
        }
    }
}
