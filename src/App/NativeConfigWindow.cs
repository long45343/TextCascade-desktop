using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 纯原生 Win32 配置窗口。
// 彻底脱离 System.Windows.Forms.Form，提供 12 项配置与登录/注销/重启等交互。
// 窗口关闭时拦截 WM_CLOSE 隐藏到托盘；右键托盘“显示主窗口”可重新唤出。
internal sealed class NativeConfigWindow : IDisposable
{
    private const string WindowClassName = "TextCascade_ConfigWindowWndClass";

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
    private readonly IntPtr _hFont;

    // 控件句柄缓存
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

    private bool _updating;
    private bool _trustCertWarningShown;
    private bool _disposed;

    public NativeConfigWindow(TrayHost app)
    {
        _app = app;
        _hFont = NativeMethods.GetStockObject(NativeMethods.DEFAULT_GUI_FONT);

        EnsureWindowClassRegistered();

        // 默认窗口尺寸 680x760
        _hwnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            "TextCascade",
            NativeMethods.WS_OVERLAPPED | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX,
            150, 100, 680, 770,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            Instances[_hwnd] = this;
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
            NativeMethods.SetWindowText(_hStatusLabel, display);
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

    private void CreateChildControls()
    {
        var hInst = NativeMethods.GetModuleHandle(null);
        var y = 14;

        // Header 标题
        CreateLabel("TextCascade", 20, y, 620, 24, isTitle: true);
        y += 28;

        // ---------- 1. 连接设置 ----------
        CreateLabel($"--- {UiText.Connection} ---", 20, y, 620, 18);
        y += 22;

        CreateLabel(UiText.ServerUrl, 20, y + 2, 140, 18);
        _hServerUrlBox = CreateEdit(IdServerUrl, 170, y, 470, 22);
        y += 28;

        CreateLabel(UiText.Username, 20, y + 2, 140, 18);
        _hUsernameBox = CreateEdit(IdUsername, 170, y, 470, 22);
        y += 28;

        CreateLabel(UiText.Password, 20, y + 2, 140, 18);
        _hPasswordBox = CreateEdit(IdPassword, 170, y, 470, 22, isPassword: true);
        y += 34;

        // ---------- 2. 安全与高级设置 ----------
        CreateLabel($"--- {UiText.SecurityAndLimits} ---", 20, y, 620, 18);
        y += 22;

        CreateLabel(UiText.HashRounds, 20, y + 2, 140, 18);
        _hHashRoundsBox = CreateEdit(IdHashRounds, 170, y, 470, 22);
        y += 28;

        CreateLabel(UiText.EncryptionSalt, 20, y + 2, 140, 18);
        _hSaltBox = CreateEdit(IdSalt, 170, y, 470, 22);
        y += 28;

        CreateLabel(UiText.LocalMaxClipboardBytes, 20, y + 2, 140, 18);
        _hLocalLimitBox = CreateEdit(IdLocalLimit, 170, y, 470, 22);
        y += 28;

        CreateLabel(UiText.ServerCertificateThumbprint, 20, y + 2, 140, 18);
        _hCertThumbprintBox = CreateEdit(IdCertThumbprint, 170, y, 470, 22);
        y += 34;

        // ---------- 3. 功能选项（复选框） ----------
        CreateLabel($"--- {UiText.Options} ---", 20, y, 620, 18);
        y += 22;

        _hCipherCheck = CreateCheckBox(IdCipherCheck, UiText.EnableEncryption, 20, y, 300, 22);
        _hSavePasswordCheck = CreateCheckBox(IdSavePasswordCheck, UiText.SavePassword, 330, y, 300, 22);
        y += 26;

        _hStartupCheck = CreateCheckBox(IdStartupCheck, UiText.StartWithWindows, 20, y, 300, 22);
        _hStatusNotificationCheck = CreateCheckBox(IdStatusNotificationCheck, UiText.WebSocketStatusNotification, 330, y, 300, 22);
        y += 26;

        _hTrustCertCheck = CreateCheckBox(IdTrustCertCheck, UiText.TrustAllCertificates, 20, y, 300, 22);
        _hUuEvasionCheck = CreateCheckBox(IdUuEvasionCheck, UiText.UuEvasion, 330, y, 300, 22);
        y += 36;

        // ---------- 4. 操作按钮 ----------
        _hBtnLogin = CreateButton(IdBtnLogin, UiText.Login, 20, y, 145, 32);
        _hBtnSave = CreateButton(IdBtnSave, UiText.Save, 175, y, 145, 32);
        _hBtnLogout = CreateButton(IdBtnLogout, UiText.Logout, 330, y, 145, 32);
        _hBtnRestart = CreateButton(IdBtnRestart, UiText.RestartService, 485, y, 145, 32);
        y += 44;

        // ---------- 5. 状态与诊断 ----------
        _hSessionLabel = CreateLabel("", 20, y, 610, 18);
        y += 22;
        _hWebsocketLabel = CreateLabel("", 20, y, 610, 18);
        y += 22;
        _hServiceLabel = CreateLabel("", 20, y, 610, 18);
        y += 24;
        _hStatusLabel = CreateLabel(UiText.Idle, 20, y, 610, 20);
    }

    private IntPtr CreateLabel(string text, int x, int y, int w, int h, bool isTitle = false)
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

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_hFont, 1);
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

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_hFont, 1);
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

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_hFont, 1);
        return hwnd;
    }

    private IntPtr CreateButton(int id, string text, int x, int y, int w, int h)
    {
        var hwnd = NativeMethods.CreateWindowEx(
            0,
            "BUTTON",
            text,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP,
            x, y, w, h,
            _hwnd,
            (IntPtr)id,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_hFont, 1);
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
                    hbrBackground = NativeMethods.GetStockObject(NativeMethods.COLOR_BTNFACE + 1),
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
            case NativeMethods.WM_CLOSE:
                // 拦截关闭：丢弃未保存修改，恢复为持久化设置，隐藏到托盘
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
        if (_hwnd != IntPtr.Zero)
        {
            Instances.TryRemove(_hwnd, out _);
            NativeMethods.DestroyWindow(_hwnd);
        }
    }
}
