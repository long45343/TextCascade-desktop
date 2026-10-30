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
        // 注销即放弃暂停意图：重新登录后应处于正常同步状态
        IsPaused = false;
        ApplyPauseState();
        PostStatus(UiText.LoggedOut);
    }

    public void StartService()
    {
        // ClipboardMonitor 是纯 Win32 消息窗口：AddClipboardFormatListener 只对
        // "创建它的那个线程"投递 WM_CLIPBOARDUPDATE。若这里跑在线程池线程上
        // （自动登录路径就是 Task.Run → LoginAsync → StartService），窗口归属一个
        // 随后退出的线程，消息再也收不到，同步静默停摆 —— 表现为"过一段时间复制
        // 没反应，重启服务才恢复"。因此必须先切回 UI 线程再建窗口。
        if (!IsOnUiThread())
        {
            _syncContext.Post(_ => StartService(), null);
            return;
        }

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

        _clipboardMonitor = new ClipboardMonitor(
            text => _engine?.SendLocalText(text, UiText.ClipboardSource),
            GetEvasionDelay);

        _serviceRunning = true;
        // 新建的引擎默认未暂停，必须按既有暂停/规避状态重新收敛，否则暂停意图会丢
        ApplyPauseState();
        if (_evasionMonitor.IsInEvasion)
        {
            PostStatus(UiText.UuEvasionActiveStatus);
        }
    }

    // 当前是否运行在宿主 UI 线程（拥有消息泵、也是所有 Win32 子窗口的归属线程）
    private bool IsOnUiThread() =>
        _syncContext.MainThreadId == Environment.CurrentManagedThreadId;

    public async Task StopServiceAsync()
    {
        // 销毁窗口必须在窗口归属线程执行，否则 DestroyWindow 会跨线程失败，
        // 留下一个仍在监听却再也收不到消息的僵尸窗口。
        if (!IsOnUiThread())
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _syncContext.Post(async _ =>
            {
                try
                {
                    await StopServiceAsync().ConfigureAwait(true);
                    tcs.TrySetResult();
                }
                catch (Exception error)
                {
                    tcs.TrySetException(error);
                }
            }, null);
            await tcs.Task.ConfigureAwait(true);
            return;
        }

        _clipboardMonitor?.Dispose();
        _clipboardMonitor = null;
        if (_engine is not null)
        {
            await _engine.StopAsync().ConfigureAwait(true);
            await _engine.DisposeAsync().ConfigureAwait(true);
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

    // 手动暂停与 UU 规避都会挂起同步，两者取并集才是真实的暂停状态。
    // 此前这两处各自设置引擎与托盘，导致规避结束时会把手动暂停一起解除，
    // 服务重启后 IsPaused 又与实际引擎状态长期不一致。
    private bool EffectivePaused => IsPaused || _evasionMonitor.IsInEvasion;

    public void TogglePause()
    {
        IsPaused = !IsPaused;
        ApplyPauseState();
        PostStatus(IsPaused ? "同步已暂停，连接保持正常" : UiText.Idle);
    }

    // 暂停状态的唯一收敛点：引擎挂起门、剪贴板监听、托盘图标与提示都在这里统一决定。
    // 所有会改变暂停含义的路径（启停服务、手动暂停、规避进出、注销）都必须调用它。
    private void ApplyPauseState()
    {
        _engine?.SetEvasionPaused(EffectivePaused);

        if (_serviceRunning && _clipboardMonitor is not null)
        {
            if (EffectivePaused)
            {
                _clipboardMonitor.Stop();
            }
            else
            {
                _clipboardMonitor.Start();
            }
        }

        if (_evasionMonitor.IsInEvasion)
        {
            _tray.SetIcon(AppIcons.EvasionTrayHandle);
            _tray.SetTip(UiText.UuEvasionTrayTooltip);
        }
        else if (IsPaused)
        {
            _tray.SetIcon(AppIcons.TrayHandle);
            _tray.SetTip("TextCascade (同步已暂停)");
        }
        else
        {
            _tray.SetIcon(AppIcons.TrayHandle);
            _tray.SetTip("TextCascade");
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
            // 规避进出只改变"规避"这一个因子；手动暂停的意图必须原样保留，
            // 因此统一交给 ApplyPauseState 取并集，而不是各自设置引擎与托盘。
            ApplyPauseState();
            if (inEvasion && _serviceRunning)
            {
                PostStatus(UiText.UuEvasionActiveStatus);
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
