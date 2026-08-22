using System.Net.NetworkInformation;
using Microsoft.Win32;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// WinForms 应用主上下文。管理：
//   - 系统托盘图标和右键菜单
//   - 主窗口 MainForm 的显示/隐藏
//   - TextSyncEngine + ClipboardMonitor 的生命周期
//   - 登录/注销流程（POST /api/v1/login + Bearer token）
//   - 电源/网络恢复时的提前重连
// 程序入口 Application.Run(new TrayApplicationContext(...)) 即创建本类实例。
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SettingsStore _settingsStore;
    private readonly NotifyIcon _trayIcon;
    private MainForm? _mainForm;
    private TextSyncEngine? _engine;
    private ClipboardMonitor? _clipboardMonitor;
    // 同步服务是否运行中（避免重复启动）
    private bool _serviceRunning;
    // 是否正在退出（防止 ExitApplication 重入）
    private bool _exiting;
    // 自动会话恢复服务（静默重登/清理会话）；手动登录/重启/注销/退出时取消在途恢复
    private readonly SessionRecoveryService _sessionRecovery;

    // 连接状态气泡节流时间戳
    private DateTimeOffset _lastBalloonAt = DateTimeOffset.MinValue;

    public TrayApplicationContext(bool launchedFromStartup)
    {
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
        _trayIcon = new NotifyIcon
        {
            Icon = AppIcons.Tray,
            Text = "TextCascade",
            Visible = true,
            ContextMenuStrip = CreateTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainForm();
        Application.Idle += StartServiceAfterMessageLoopStarts;
        // 方案 A：托盘常驻也先创建主窗体但不显示，保证后台线程调用
        // StartServiceAsync 时永远有稳定的 BeginInvoke/InvokeRequired 目标。
        EnsureMainFormCreated();
        if (!launchedFromStartup)
        {
            _mainForm!.Show();
        }

        // 电源/网络恢复：提前触发重连（与 Android 端 ACTION_USER_PRESENT 行为对齐）
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public SettingsStore SettingsStore => _settingsStore;

    public bool ServiceRunning => _serviceRunning;

    public bool IsLoggedIn => HasServiceSession(_settingsStore.Data);

    // UI/自动登录的对外入口：任何新的显式登录都优先于在途恢复。
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

        // 密码来源：用户输入优先，否则复用已保存的密码（内存中为明文）
        var typedPassword = request.Password;
        if (string.IsNullOrWhiteSpace(typedPassword))
        {
            if (!data.SavePassword || string.IsNullOrWhiteSpace(data.SavedPassword))
            {
                throw new InvalidOperationException(UiText.RequiredLoginFields);
            }
            typedPassword = data.SavedPassword;
        }

        // PBKDF2 较慢，放到线程池执行，避免登录期间 UI 假死。
        // 派生密钥持久化（DPAPI 保护）以支持“不保存原始密码仍可解密”
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
            cancellationToken).ConfigureAwait(true);

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

        // 登录成功即获得新会话。必须先停掉可能残留的旧引擎，再启动新引擎；
        // 否则 StartService 的 _serviceRunning 早退会让新凭据永不生效。
        await StopServiceAsync().ConfigureAwait(true);
        StartService();
        return result;
    }

    // 注销：以 close code 1000 正常关闭引擎并清空会话。
    // 新协议无 HTTP 注销端点，token 由服务端自然过期
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
        // StartService touches UI-bound objects (Clipboard, SynchronizationContext)
        // and creates the engine that posts back to the UI thread. Always marshal
        // to the UI thread first so the captured SynchronizationContext is the
        // real message-loop one instead of a synthetic fallback.
        if (_mainForm is { IsDisposed: false, InvokeRequired: true })
        {
            _mainForm.BeginInvoke(StartService);
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

        // On the UI thread the current sync context is the WinForms message loop.
        // If it is null we are in an invalid state; throw instead of silently
        // creating a detached context whose Post never runs.
        var context = SynchronizationContext.Current
            ?? throw new InvalidOperationException("StartService must be called on the UI thread.");

        _engine = new TextSyncEngine(
            ClipConfig.FromSettings(_settingsStore),
            context,
            PostStatus,
            _ => PostStatus(UiText.RemoteTextApplied),
            () => _sessionRecovery.RunAsync(BuildRecoveryRequest()),
            OnConnectionChanged);
        _engine.Start();

        RunOnUi(() =>
        {
            _clipboardMonitor = new ClipboardMonitor(text => _engine?.SendLocalText(text, UiText.ClipboardSource));
            _clipboardMonitor.Start();
        });
        _serviceRunning = true;
        RefreshUi();
    }

    public async Task StopServiceAsync()
    {
        RunOnUi(() =>
        {
            _clipboardMonitor?.Dispose();
            _clipboardMonitor = null;
        });
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

    public void SaveSettings()
    {
        _settingsStore.Save();
    }

    public void SetStartup(bool enabled)
    {
        StartupManager.SetEnabled(enabled);
        _settingsStore.Data.RelaunchOnBoot = enabled;
        _settingsStore.Save();
    }

    // clientId：UUID v4，首次运行生成并持久化（§5.2 长度 1-128）；
    // clientName：机器名（§5.2 长度 0-128，超长截断）
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

    private void EnsureMainFormCreated()
    {
        if (_mainForm is null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm(this);
            _mainForm.FormClosing += (_, args) =>
            {
                if (_exiting)
                {
                    return;
                }
                args.Cancel = true;
                _mainForm.Hide();
            };
        }
    }

    public void ShowMainForm()
    {
        EnsureMainFormCreated();

        _mainForm!.Show();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
    }

    // Public API entry point invoked by the tray menu. Must remain `async void`
    // because ToolStripMenuItem.Click handlers are sync, but we guard against
    // any unhandled exception so a failure during shutdown does not leave the
    // tray icon orphaned.
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
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            await StopServiceAsync().ConfigureAwait(true);
            _settingsStore.Save();
        }
        catch
        {
            // Best-effort shutdown; swallow errors so the process can exit.
        }
        _mainForm?.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _trayIcon.Dispose();
            _clipboardMonitor?.Dispose();
        }
        base.Dispose(disposing);
    }

    // 睡眠/休眠恢复：若引擎处于退避等待，立即（1-2s 内）触发重连
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _engine?.NotifyWake();
        }
    }

    // 网络恢复可用：同上提前重连
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            _engine?.NotifyWake();
        }
    }

    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add(UiText.Show, null, (_, _) => ShowMainForm());
            var restartItem = menu.Items.Add(UiText.RestartService, null, async (_, _) =>
            {
                try
                {
                    await RestartServiceAsync().ConfigureAwait(true);
                }
                catch (Exception error)
                {
                    PostStatus(UiText.RestartServiceFailed(error.Message));
                }
            });
            restartItem.Enabled = IsLoggedIn;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(UiText.Exit, null, (_, _) => ExitApplication());
        };
        return menu;
    }

    private async void StartServiceAfterMessageLoopStarts(object? sender, EventArgs args)
    {
        Application.Idle -= StartServiceAfterMessageLoopStarts;
        // Surface a corrupted settings file instead of silently resetting.
        // Done here so the MainForm is already alive and the status label
        // can actually receive the message.
        if (!string.IsNullOrWhiteSpace(_settingsStore.LoadError))
        {
            PostStatus(UiText.SettingsLoadFailed(_settingsStore.LoadError));
        }

        var data = _settingsStore.Data;

        // 有保存的密码时，重新登录获取新 token（旧 token 在重启后通常已过期）
        if (data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPassword)
            && !string.IsNullOrWhiteSpace(data.ServerUrl)
            && !string.IsNullOrWhiteSpace(data.Username))
        {
            if (_exiting)
            {
                return;
            }
            PostStatus(UiText.AutoLogin);
            try
            {
                var request = new LoginRequest(
                    data.ServerUrl,
                    data.Username,
                    data.SavedPassword,
                    data.HashRounds,
                    data.Salt,
                    data.TrustAllCertificates);
                await LoginAsync(request, CancellationToken.None).ConfigureAwait(true);
                if (_exiting)
                {
                    return;
                }
                PostStatus(UiText.LoginSuccessful);
            }
            catch (Exception error)
            {
                PostStatus(UiText.AutoLoginFailed(error.Message));
                // 登录失败时尝试用旧 session 启动（token 可能仍然有效）
                if (IsLoggedIn)
                {
                    StartService();
                }
                else
                {
                    RefreshUi();
                }
            }
        }
        else if (IsLoggedIn)
        {
            // 没有保存密码但有旧 token，尝试直接启动
            StartService();
        }
        else
        {
            RefreshUi();
        }
    }

    // 构建会话恢复请求：保存密码存在则返回该请求，否则返回 null（由 Service 停止服务并清理会话）
    private LoginRequest? BuildRecoveryRequest()
    {
        var data = _settingsStore.Data;
        return data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPassword)
            ? new LoginRequest(data.ServerUrl, data.Username, data.SavedPassword,
                               data.HashRounds, data.Salt, data.TrustAllCertificates)
            : null;
    }

    // 版本游标推进（来自引擎线程池线程）：切回 UI 线程写回 settings.json。
    // 原子写 + 人类复制频率 → 每次推进直接落盘即可
    private void OnServerVersionAdvanced(ulong version)
    {
        PostToUi(() =>
        {
            _settingsStore.Data.LastServerVersion = version;
            _settingsStore.Save();
        });
    }

    // 引擎回调来自线程池：开关开启且有节流余量时切回 UI 线程弹气泡
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
        PostToUi(() => _trayIcon.ShowBalloonTip(3000, "TextCascade", text, ToolTipIcon.Info));
    }

    // 主窗体不存在时直接丢弃 UI 动作
    private void PostToUi(Action action)
    {
        if (_mainForm is not { IsDisposed: false })
        {
            return;
        }
        if (_mainForm.InvokeRequired)
        {
            _mainForm.BeginInvoke(action);
            return;
        }
        action();
    }

    private void RunOnUi(Action action) => PostToUi(action);

    private void PostStatus(string message)
    {
        PostToUi(() => _mainForm?.SetStatus(message));
    }

    private void RefreshUi()
    {
        PostToUi(() => _mainForm?.RefreshFromState());
    }

    private static bool HasServiceSession(SettingsData data)
    {
        return !string.IsNullOrWhiteSpace(data.AuthToken);
    }
}

