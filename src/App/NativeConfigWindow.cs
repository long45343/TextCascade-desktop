using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 具备 Windows 11 Fluent Design 风格的纯原生 Win32 配置窗口。
// 特性：
//   - 单列通栏圆角大卡片（Single-Column Fluent Cards）
//   - 动态字阶系统（Segoe UI Variable / Segoe UI 高清平滑抗锯齿）
//   - 自动跟随 Windows 11 深浅色主题（DWM Immersive Dark Mode + 配色自适应）
//   - Accent 主题色圆角主按钮自绘
//   - 彻底脱离 System.Windows.Forms，天然 NativeAOT 零反射、零额外依赖。
internal sealed class NativeConfigWindow : IDisposable
{
    private const string WindowClassName = "TextCascade_FluentConfigWndClass";

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
    private IntPtr _fontSubtitle;
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

    // 界面状态与画刷缓存
    private bool _isDarkMode;
    private bool _updating;
    private bool _trustCertWarningShown;
    private bool _disposed;

    // GDI 资源
    private IntPtr _hBrushBg;
    private IntPtr _hBrushCard;
    private IntPtr _hPenBorder;

    // 卡片矩形坐标
    private NativeMethods.RECT _rcCard1;
    private NativeMethods.RECT _rcCard2;
    private NativeMethods.RECT _rcCard3;

    public NativeConfigWindow(TrayHost app)
    {
        _app = app;
        _isDarkMode = DetectIsDarkMode();

        EnsureWindowClassRegistered();
        InitializeFonts();
        UpdateThemeBrushes();

        // 现代窗口尺寸：宽 700，高 830，居中呈现
        _hwnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            "TextCascade",
            NativeMethods.WS_OVERLAPPED | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX,
            120, 80, 700, 840,
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

        // 标题：18pt 粗体
        _fontTitle = NativeMethods.CreateFont(
            -24, 0, 0, 0, NativeMethods.FW_BOLD, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        // 卡片分组：11pt 半粗体
        _fontSubtitle = NativeMethods.CreateFont(
            -15, 0, 0, 0, NativeMethods.FW_SEMIBOLD, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        // 正文/按钮：9.5pt 常规
        _fontBody = NativeMethods.CreateFont(
            -13, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, 0, 0, NativeMethods.CLEARTYPE_QUALITY, 0, fontFace);

        // 说明/辅助：8.5pt
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

        // 浅色模式: 背景 #F3F3F3, 卡片 #FFFFFF, 边框 #E5E5E5
        // 深色模式: 背景 #202020, 卡片 #2D2D2D, 边框 #3A3A3A
        var bgCol = _isDarkMode ? 0x00202020U : 0x00F3F3F3U;
        var cardCol = _isDarkMode ? 0x002D2D2DU : 0x00FFFFFFU;
        var borderCol = _isDarkMode ? 0x003A3A3AU : 0x00E5E5E5U;

        _hBrushBg = NativeMethods.CreateSolidBrush(bgCol);
        _hBrushCard = NativeMethods.CreateSolidBrush(cardCol);
        _hPenBorder = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, borderCol);
    }

    private void CreateChildControls()
    {
        var cardX = 22;
        var cardW = 638;

        // ---------- 1. 顶部 Header ----------
        var y = 16;
        CreateLabel("TextCascade", cardX, y, 300, 28, _fontTitle);
        CreateLabel("v2.5.1.0 • Fluent Native", cardX + 160, y + 8, 200, 18, _fontCaption);
        y += 36;

        // ---------- 2. 卡片 1：连接与认证 ----------
        var card1Top = y;
        _rcCard1 = new NativeMethods.RECT { Left = cardX, Top = card1Top, Right = cardX + cardW, Bottom = card1Top + 140 };

        CreateLabel(UiText.Connection, cardX + 16, card1Top + 12, 200, 20, _fontSubtitle);

        var innerY = card1Top + 40;
        CreateLabel(UiText.ServerUrl, cardX + 18, innerY + 3, 130, 20, _fontBody);
        _hServerUrlBox = CreateEdit(IdServerUrl, cardX + 155, innerY, 460, 24);
        innerY += 30;

        CreateLabel(UiText.Username, cardX + 18, innerY + 3, 130, 20, _fontBody);
        _hUsernameBox = CreateEdit(IdUsername, cardX + 155, innerY, 460, 24);
        innerY += 30;

        CreateLabel(UiText.Password, cardX + 18, innerY + 3, 130, 20, _fontBody);
        _hPasswordBox = CreateEdit(IdPassword, cardX + 155, innerY, 460, 24, isPassword: true);

        y = _rcCard1.Bottom + 14;

        // ---------- 3. 卡片 2：安全与限制 ----------
        var card2Top = y;
        _rcCard2 = new NativeMethods.RECT { Left = cardX, Top = card2Top, Right = cardX + cardW, Bottom = card2Top + 170 };

        CreateLabel(UiText.SecurityAndLimits, cardX + 16, card2Top + 12, 250, 20, _fontSubtitle);

        innerY = card2Top + 40;
        CreateLabel(UiText.HashRounds, cardX + 18, innerY + 3, 130, 20, _fontBody);
        _hHashRoundsBox = CreateEdit(IdHashRounds, cardX + 155, innerY, 460, 24);
        innerY += 30;

        CreateLabel(UiText.EncryptionSalt, cardX + 18, innerY + 3, 130, 20, _fontBody);
        _hSaltBox = CreateEdit(IdSalt, cardX + 155, innerY, 460, 24);
        innerY += 30;

        CreateLabel(UiText.LocalMaxClipboardBytes, cardX + 18, innerY + 3, 130, 20, _fontBody);
        _hLocalLimitBox = CreateEdit(IdLocalLimit, cardX + 155, innerY, 460, 24);
        innerY += 30;

        CreateLabel(UiText.ServerCertificateThumbprint, cardX + 18, innerY + 3, 130, 20, _fontBody);
        _hCertThumbprintBox = CreateEdit(IdCertThumbprint, cardX + 155, innerY, 460, 24);

        y = _rcCard2.Bottom + 14;

        // ---------- 4. 卡片 3：功能选项 ----------
        var card3Top = y;
        _rcCard3 = new NativeMethods.RECT { Left = cardX, Top = card3Top, Right = cardX + cardW, Bottom = card3Top + 130 };

        CreateLabel(UiText.Options, cardX + 16, card3Top + 12, 200, 20, _fontSubtitle);

        innerY = card3Top + 40;
        _hCipherCheck = CreateCheckBox(IdCipherCheck, UiText.EnableEncryption, cardX + 20, innerY, 280, 24);
        _hSavePasswordCheck = CreateCheckBox(IdSavePasswordCheck, UiText.SavePassword, cardX + 320, innerY, 280, 24);
        innerY += 28;

        _hStartupCheck = CreateCheckBox(IdStartupCheck, UiText.StartWithWindows, cardX + 20, innerY, 280, 24);
        _hStatusNotificationCheck = CreateCheckBox(IdStatusNotificationCheck, UiText.WebSocketStatusNotification, cardX + 320, innerY, 280, 24);
        innerY += 28;

        _hTrustCertCheck = CreateCheckBox(IdTrustCertCheck, UiText.TrustAllCertificates, cardX + 20, innerY, 280, 24);
        _hUuEvasionCheck = CreateCheckBox(IdUuEvasionCheck, UiText.UuEvasion, cardX + 320, innerY, 280, 24);

        y = _rcCard3.Bottom + 18;

        // ---------- 5. 核心操作按钮（Accent 蓝底自绘） ----------
        _hBtnLogin = CreateButton(IdBtnLogin, UiText.Login, cardX, y, 145, 34, isAccent: true);
        _hBtnSave = CreateButton(IdBtnSave, UiText.Save, cardX + 160, y, 145, 34, isAccent: true);
        _hBtnLogout = CreateButton(IdBtnLogout, UiText.Logout, cardX + 325, y, 145, 34, isAccent: false);
        _hBtnRestart = CreateButton(IdBtnRestart, UiText.RestartService, cardX + 485, y, 150, 34, isAccent: false);

        y += 48;

        // ---------- 6. 底部状态与诊断栏 ----------
        _hSessionLabel = CreateLabel("", cardX, y, 320, 18, _fontCaption);
        _hWebsocketLabel = CreateLabel("", cardX + 330, y, 310, 18, _fontCaption);
        y += 22;
        _hServiceLabel = CreateLabel("", cardX, y, 320, 18, _fontCaption);
        _hStatusLabel = CreateLabel($"{UiText.Status}: {UiText.Idle}", cardX + 330, y, 310, 18, _fontCaption);
    }

    private IntPtr CreateLabel(string text, int x, int y, int w, int h, IntPtr hFont)
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
            NativeMethods.WS_EX_CLIENTEDGE,
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
                    hbrBackground = IntPtr.Zero, // 自绘背景，避免闪烁
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

    // 绘制 Fluent 纯色底色与圆角卡片
    private unsafe void OnPaint()
    {
        var hdc = NativeMethods.BeginPaint(_hwnd, out var ps);
        try
        {
            // 1. 窗口底色铺满
            NativeMethods.FillRect(hdc, in ps.rcPaint, _hBrushBg);

            // 2. 绘制三张 Fluent 单列圆角卡片
            DrawCard(hdc, in _rcCard1);
            DrawCard(hdc, in _rcCard2);
            DrawCard(hdc, in _rcCard3);
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

        // Windows 11 Fluent 规范：卡片圆角 8px
        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 12, 12);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
    }

    private nint OnCtlColorStatic(nuint wParam, nint lParam)
    {
        var hdc = (IntPtr)wParam;
        var hwndCtrl = (IntPtr)lParam;

        NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);

        // 文本颜色：深色模式纯白/浅灰，浅色模式深黑/中灰
        var textColor = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
        if (hwndCtrl == _hSessionLabel || hwndCtrl == _hWebsocketLabel ||
            hwndCtrl == _hServiceLabel || hwndCtrl == _hStatusLabel)
        {
            textColor = _isDarkMode ? 0x009E9E9EU : 0x005F5F5FU;
        }

        NativeMethods.SetTextColor(hdc, textColor);

        // 判断是否在卡片内部，返回对应卡片画刷，否则返回外层底色画刷
        return IsInsideAnyCard(hwndCtrl) ? _hBrushCard : _hBrushBg;
    }

    private bool IsInsideAnyCard(IntPtr hwndCtrl)
    {
        // 外部状态栏和标题栏位于卡片外
        return hwndCtrl != _hSessionLabel && hwndCtrl != _hWebsocketLabel
            && hwndCtrl != _hServiceLabel && hwndCtrl != _hStatusLabel;
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

        // Windows 11 Accent 蓝色调：正常 #0067C0，按压微暗 #005FB8，禁用微灰
        uint btnCol;
        if (isDisabled)
        {
            btnCol = _isDarkMode ? 0x003A3A3AU : 0x00CCCCCCU;
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

        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, 8, 8);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);

        // 按钮文本居中白字
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
        if (_fontSubtitle != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_fontSubtitle);
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

        if (_hwnd != IntPtr.Zero)
        {
            Instances.TryRemove(_hwnd, out _);
            NativeMethods.DestroyWindow(_hwnd);
        }
    }
}
