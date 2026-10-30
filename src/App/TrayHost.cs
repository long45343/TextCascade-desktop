using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 原生 Win32 应用主宿主，管理：
//   - 主线程隐藏宿主 HWND 与 Win32SynchronizationContext 消息分发
//   - 原生系统托盘图标（NativeTray）与右键菜单
//   - 原生主配置窗口（NativeConfigWindow）的生命周期
//   - TextSyncEngine + ClipboardMonitor 的生命周期
//   - 登录/注销流程与会话恢复（SessionRecoveryService）
//   - UU 远程规避监控器（UuRemoteEvasionMonitor）
//   - 电源/网络恢复时的提前重连
// 彻底脱离 System.Windows.Forms.ApplicationContext，天然原生 AOT。
internal sealed class TrayHost : IDisposable
{
    private const string WindowClassName = "TextCascade_TrayHostWndClass";

    private static readonly ConcurrentDictionary<IntPtr, TrayHost> Instances = new();
    private static readonly NativeMethods.WndProc StaticWndProc = WindowProcedure;
    private static readonly object RegisterLock = new();
    private static bool _classRegistered;

    private readonly IntPtr _hwnd;
    private readonly Win32SynchronizationContext _syncContext;
    private readonly SettingsStore _settingsStore;
    private readonly NativeTray _tray;
    private readonly NativeConfigWindow _configWindow;
    private readonly SessionRecoveryService _sessionRecovery;
    private readonly UuRemoteEvasionMonitor _evasionMonitor;

    private TextSyncEngine? _engine;
    private ClipboardMonitor? _clipboardMonitor;
    private bool _serviceRunning;
    private bool _exiting;
    private bool _disposed;
    private DateTimeOffset _lastBalloonAt = DateTimeOffset.MinValue;

    public TrayHost(bool launchedFromStartup)
    {
        EnsureWindowClassRegistered();

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            "TextCascadeTrayHost",
            0,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            Instances[_hwnd] = this;
        }

        _syncContext = new Win32SynchronizationContext(_hwnd);
        SynchronizationContext.SetSynchronizationContext(_syncContext);

        _settingsStore = SettingsStore.LoadDefault();
        _settingsStore.Data.RelaunchOnBoot = StartupManager.IsEnabled();
        if (_settingsStore.Data.RelaunchOnBoot)
        {
            StartupManager.NormalizeEnabledEntry();
        }
        EnsureClientIdAndName();

        _sessionRecovery = new SessionRecoveryService(
            loginAsync: (req, ct) => LoginCoreAsync(req, ct),
            stopServiceAsync: StopServiceAsync,
            clearSession: () =>
            {
                _settingsStore.ClearSession();
                _settingsStore.Save();
            },
            postStatus: PostStatus,
            refreshUi: RefreshUi);

        _evasionMonitor = new UuRemoteEvasionMonitor(
            isEnabled: () => _settingsStore.Data.UuEvasionEnabled,
            getTargetProcesses: () => _settingsStore.Data.UuEvasionProcessNames is { Count: > 0 } names
                ? names
                : SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: OnEvasionStateChanged);
        _evasionMonitor.Start();

        _tray = new NativeTray(
            _hwnd,
            onShow: ShowMainForm,
            onRestart: () => _ = RestartServiceAsync(),
            onLogout: () => _ = LogoutAsync(CancellationToken.None),
            onExit: ExitApplication,
            isLoggedIn: () => IsLoggedIn);

        _configWindow = new NativeConfigWindow(this);

        if (!launchedFromStartup)
        {
            _configWindow.Show();
        }

        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        // 异步派发启动服务逻辑
        _syncContext.Post(_ => StartServiceInitial(), null);
    }

    public IntPtr Handle => _hwnd;
    public SettingsStore SettingsStore => _settingsStore;
    public bool ServiceRunning => _serviceRunning;
    public bool IsLoggedIn => HasServiceSession(_settingsStore.Data);

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        _sessionRecovery.Cancel();
        return await LoginCoreAsync(request, cancellationToken).ConfigureAwait(true);
    }

    private async Task<LoginResult> LoginCoreAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var data = _settingsStore.Data;
        if (string.IsNullOrWhiteSpace(request.ServerUrl) || string.IsNullOrWhiteSpace(request.Username))
        {
            throw new InvalidOperationException(UiText.RequiredLoginFields);
        }

        var typedPassword = request.Password;
        if (string.IsNullOrWhiteSpace(typedPassword))
        {
            if (!data.SavePassword || string.IsNullOrWhiteSpace(data.SavedPassword))
            {
                throw new InvalidOperationException(UiText.RequiredLoginFields);
            }
            typedPassword = data.SavedPassword;
        }

        var keyBase64 = data.CipherEnabled
            ? Convert.ToBase64String(await Task.Run(
                () => CryptoManager.DerivePasswordKey(request.Username, typedPassword, request.Salt, request.HashRounds),
                cancellationToken).ConfigureAwait(true))
            : string.Empty;

        var client = new ClipApiClient();
        var result = await client.LoginAsync(
            request.ServerUrl,
            request.Username,
            typedPassword,
            request.TrustAllCertificates,
            cancellationToken,
            serverCertificateThumbprint: request.ServerCertificateThumbprint).ConfigureAwait(true);

        data.ServerUrl = result.NormalizedServerUrl;
        data.Username = request.Username.Trim();
        data.HashRounds = request.HashRounds;
        data.Salt = request.Salt;
        data.TrustAllCertificates = request.TrustAllCertificates;
        data.DerivedKeyBase64 = keyBase64;
        data.AuthToken = result.Token;
        data.TokenExpiresAtUtc = JsonUtil.Rfc3339Utc(result.ExpiresAtUtc);
        data.ProtocolVersion = result.ProtocolVersion;
        data.MaxTextBytes = result.MaxTextBytes;
        data.HelloTimeoutSeconds = result.HelloTimeoutSeconds;
        data.HeartbeatIntervalSeconds = result.HeartbeatIntervalSeconds;
        data.HeartbeatTimeoutSeconds = result.HeartbeatTimeoutSeconds;
        data.SavedPassword = data.SavePassword ? typedPassword : string.Empty;
        EnsureClientIdAndName();
        _settingsStore.Save();

        await StopServiceAsync().ConfigureAwait(true);
        StartService();
        return result;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        _sessionRecovery.Cancel();
        await StopServiceAsync().ConfigureAwait(true);
        _settingsStore.ClearSession();
        _settingsStore.Save();
        PostStatus(UiText.LoggedOut);
    }

    public void StartService()
    {
        if (_serviceRunning)
        {
            return;
        }

        var data = _settingsStore.Data;
        if (!HasServiceSession(data))
        {
            PostStatus(UiText.LoginFirst);
            RefreshUi();
            return;
        }

        _engine = new TextSyncEngine(
            ClipConfig.FromSettings(_settingsStore),
            _syncContext,
            PostStatus,
            _ => PostStatus(UiText.RemoteTextApplied),
            () => _sessionRecovery.RunAsync(BuildRecoveryRequest()),
            OnConnectionChanged,
            onServerVersionAdvanced: OnServerVersionAdvanced,
            getEvasionDelay: GetEvasionDelay);
        _engine.Start();
        if (_evasionMonitor.IsInEvasion)
        {
            _engine.SetEvasionPaused(true);
        }

        _clipboardMonitor = new ClipboardMonitor(
            text => _engine?.SendLocalText(text, UiText.ClipboardSource),
            GetEvasionDelay);
        if (!_evasionMonitor.IsInEvasion)
        {
            _clipboardMonitor.Start();
        }

        _serviceRunning = true;
        if (_evasionMonitor.IsInEvasion)
        {
            _tray.SetIcon(AppIcons.EvasionTrayHandle);
            _tray.SetTip(UiText.UuEvasionTrayTooltip);
            PostStatus(UiText.UuEvasionActiveStatus);
        }
        RefreshUi();
    }

    public async Task StopServiceAsync()
    {
        _clipboardMonitor?.Dispose();
        _clipboardMonitor = null;
        if (_engine is not null)
        {
            await _engine.StopAsync().ConfigureAwait(false);
            await _engine.DisposeAsync().ConfigureAwait(false);
            _engine = null;
        }
        _serviceRunning = false;
        RefreshUi();
    }

    public async Task RestartServiceAsync()
    {
        _sessionRecovery.Cancel();
        await StopServiceAsync().ConfigureAwait(true);
        StartService();
    }

    public bool IsPaused { get; private set; }

    public void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused)
        {
            _clipboardMonitor?.Stop();
            _engine?.SetEvasionPaused(true);
            _tray.SetTip("TextCascade (同步已暂停)");
            PostStatus("同步已暂停，连接保持正常");
        }
        else
        {
            _engine?.SetEvasionPaused(false);
            if (_serviceRunning && !_evasionMonitor.IsInEvasion)
            {
                _clipboardMonitor?.Start();
            }
            _tray.SetIcon(AppIcons.TrayHandle);
            _tray.SetTip("TextCascade");
            PostStatus(UiText.Idle);
        }
        RefreshUi();
    }

    public void SaveSettings()
    {
        _settingsStore.Save();
    }

    public void ShowMainForm()
    {
        _configWindow.Show();
    }

    public async void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        try
        {
            _sessionRecovery.Cancel();
            _evasionMonitor.Dispose();
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _tray.Dispose();
            await StopServiceAsync().ConfigureAwait(true);
            _settingsStore.Save();
        }
        catch
        {
        }
        _configWindow.Dispose();
        NativeMethods.PostQuitMessage(0);
    }

    private void StartServiceInitial()
    {
        if (!string.IsNullOrWhiteSpace(_settingsStore.LoadError))
        {
            PostStatus(UiText.SettingsLoadFailed(_settingsStore.LoadError));
        }

        var data = _settingsStore.Data;
        if (data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPassword)
            && !string.IsNullOrWhiteSpace(data.ServerUrl)
            && !string.IsNullOrWhiteSpace(data.Username))
        {
            if (_exiting)
            {
                return;
            }
            PostStatus(UiText.AutoLogin);
            _ = Task.Run(async () =>
            {
                try
                {
                    var request = new LoginRequest(
                        data.ServerUrl,
                        data.Username,
                        data.SavedPassword,
                        data.HashRounds,
                        data.Salt,
                        data.TrustAllCertificates,
                        data.ServerCertificateThumbprint);
                    await LoginAsync(request, CancellationToken.None).ConfigureAwait(true);
                    if (_exiting)
                    {
                        return;
                    }
                    PostStatus(UiText.LoginSuccessful);
                }
                catch (Exception error)
                {
                    PostStatus(CoreStatus.Pack(ErrorCodes.AutoLoginFailed, error.Message));
                    if (IsLoggedIn)
                    {
                        _syncContext.Post(_ => StartService(), null);
                    }
                    else
                    {
                        RefreshUi();
                    }
                }
            });
        }
        else if (IsLoggedIn)
        {
            StartService();
        }
        else
        {
            RefreshUi();
        }
    }

    private TimeSpan GetEvasionDelay()
    {
        if (!_settingsStore.Data.UuEvasionEnabled || !_evasionMonitor.IsProcessRunning)
        {
            return TimeSpan.Zero;
        }
        var ms = _settingsStore.Data.UuEvasionDelayMs;
        return ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromMilliseconds(200);
    }

    private void OnEvasionStateChanged(bool inEvasion)
    {
        _syncContext.Post(_ =>
        {
            if (inEvasion)
            {
                _clipboardMonitor?.Stop();
                _engine?.SetEvasionPaused(true);
                _tray.SetIcon(AppIcons.EvasionTrayHandle);
                _tray.SetTip(UiText.UuEvasionTrayTooltip);
                PostStatus(UiText.UuEvasionActiveStatus);
            }
            else
            {
                _engine?.SetEvasionPaused(false);
                if (_serviceRunning)
                {
                    _clipboardMonitor?.Start();
                }
                _tray.SetIcon(AppIcons.TrayHandle);
                _tray.SetTip("TextCascade");
                PostStatus(UiText.Idle);
                RefreshUi();
            }
        }, null);
    }

    private void EnsureClientIdAndName()
    {
        var data = _settingsStore.Data;
        if (string.IsNullOrWhiteSpace(data.ClientId))
        {
            data.ClientId = Guid.NewGuid().ToString();
            _settingsStore.Save();
        }
        if (string.IsNullOrWhiteSpace(data.ClientName))
        {
            data.ClientName = Environment.MachineName.Length > 128
                ? Environment.MachineName[..128]
                : Environment.MachineName;
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            _engine?.NotifyWake();
        }
    }

    private LoginRequest? BuildRecoveryRequest()
    {
        var data = _settingsStore.Data;
        return data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPassword)
            ? new LoginRequest(data.ServerUrl, data.Username, data.SavedPassword,
                               data.HashRounds, data.Salt, data.TrustAllCertificates,
                               data.ServerCertificateThumbprint)
            : null;
    }

    private void OnServerVersionAdvanced(ulong version)
    {
        _syncContext.Post(_ =>
        {
            _settingsStore.Data.LastServerVersion = version;
            _settingsStore.Save();
        }, null);
    }

    private void OnConnectionChanged(bool connected)
    {
        if (!_settingsStore.Data.WebsocketStatusNotification)
        {
            return;
        }
        if (DateTimeOffset.UtcNow - _lastBalloonAt < TimeSpan.FromSeconds(30))
        {
            return;
        }
        _lastBalloonAt = DateTimeOffset.UtcNow;
        var text = connected ? UiText.Connected : UiText.Disconnected(string.Empty);
        _syncContext.Post(_ => _tray.ShowBalloon("TextCascade", text), null);
    }

    private void PostStatus(string message)
    {
        _syncContext.Post(_ => _configWindow.SetStatus(message), null);
    }

    // 配置窗口不可见时的失败反馈出口：走系统托盘气泡，避免用户完全看不到
    public void NotifyProblem(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        _syncContext.Post(_ =>
        {
            if (DateTimeOffset.UtcNow - _lastBalloonAt < TimeSpan.FromSeconds(10))
            {
                return;
            }
            _lastBalloonAt = DateTimeOffset.UtcNow;
            _tray.ShowBalloon("TextCascade", text, NativeMethods.NIIF_WARNING);
        }, null);
    }

    private void RefreshUi()
    {
        _syncContext.Post(_ => _configWindow.RefreshFromState(), null);
    }

    private static bool HasServiceSession(SettingsData data)
    {
        return !string.IsNullOrWhiteSpace(data.AuthToken);
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
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
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
        if (Instances.TryGetValue(hWnd, out var host))
        {
            return host.HandleMessage(hWnd, msg, wParam, lParam);
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private nint HandleMessage(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_APP_DISPATCH:
                _syncContext.ProcessQueue();
                return 0;

            case NativeMethods.WM_APP_TRAYMSG:
                _tray.HandleTrayMessage(lParam);
                return 0;

            case NativeMethods.WM_POWERBROADCAST:
                if (wParam == NativeMethods.PBT_APMRESUMEAUTOMATIC || wParam == NativeMethods.PBT_APMRESUMESUSPEND)
                {
                    _engine?.NotifyWake();
                }
                return 0;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ExitApplication();
        if (_hwnd != IntPtr.Zero)
        {
            Instances.TryRemove(_hwnd, out _);
            NativeMethods.DestroyWindow(_hwnd);
        }
    }
}
