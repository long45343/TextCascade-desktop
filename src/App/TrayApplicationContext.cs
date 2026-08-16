using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// WinForms 应用主上下文。管理：
//   - 系统托盘图标和右键菜单
//   - 主窗口 MainForm 的显示/隐藏
//   - TextSyncEngine + ClipboardMonitor 的生命周期
//   - 登录/注销流程（通过 ClipApiClient）
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
    // 会话失效后的静默重登是否已尝试（每次手动登录/重启服务时复位）
    private int _sessionRecoveryAttempted;
    // 当前在途的会话恢复任务；手动登录/重启/注销/退出时取消，避免旧恢复覆盖新会话
    private CancellationTokenSource? _sessionRecoveryCts;

    private const int SessionRecoveryMaxAttempts = 5;
    private static readonly TimeSpan[] SessionRecoveryDelays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30)
    };
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
    }

    public SettingsStore SettingsStore => _settingsStore;

    public bool ServiceRunning => _serviceRunning;

    public bool IsLoggedIn => HasServiceSession(_settingsStore.Data);

    // UI/自动登录的对外入口：任何新的显式登录都优先于在途恢复。
    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        CancelSessionRecovery();
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

        // 每次登录都根据当前参数重新计算 SHA3 hash 和 AES 密钥
        // 手工/UI 发起的登录复位会话恢复次数；引擎线程的静默重登不会触发
        // SHA3 与 PBKDF2 都较慢，放到线程池执行，避免登录期间 UI 假死
        var passwordSha3 = await Task.Run(() => CryptoManager.Sha3_512LowercaseHex(typedPassword), cancellationToken).ConfigureAwait(true);
        var keyBase64 = data.CipherEnabled
            ? Convert.ToBase64String(await Task.Run(
                () => CryptoManager.DerivePasswordKey(request.Username, typedPassword, request.Salt, request.HashRounds),
                cancellationToken).ConfigureAwait(true))
            : string.Empty;

        var client = new ClipApiClient();
        var result = await client.LoginAsync(
            request.ServerUrl,
            request.Username,
            passwordSha3,
            cancellationToken);

        data.ServerUrl = result.NormalizedServerUrl;
        data.Username = request.Username.Trim();
        data.HashRounds = request.HashRounds;
        data.Salt = request.Salt;
        data.HashedPasswordBase64 = keyBase64;
        data.CookieHeader = result.CookieHeader;
        data.WebsocketUrl = result.WebsocketUrl;
        data.CsrfToken = result.CsrfToken;
        data.MaxSizeBytes = result.MaxSizeBytes;
        data.SavedPassword = data.SavePassword ? typedPassword : string.Empty;
        _settingsStore.Save();

        // 登录成功即获得新会话。必须先停掉可能残留的旧引擎，再启动新引擎；
        // 否则 StartService 的 _serviceRunning 早退会让新凭据永不生效。
        ResetSessionRecovery();
        await StopServiceAsync().ConfigureAwait(true);
        StartService();
        return result;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        CancelSessionRecovery();
        var data = _settingsStore.Data;
        try
        {
            await new ClipApiClient().LogoutAsync(data.ServerUrl, data.CookieHeader, data.CsrfToken, cancellationToken);
        }
        catch
        {
            // 注销请求失败不阻断本地清理
        }

        await StopServiceAsync();
        _settingsStore.ClearSession();
        _settingsStore.Save();
        PostStatus(UiText.LoggedOut);
    }

    public void StartService()
    {
        // StartService touches UI-bound objects (Clipboard, SynchronizationContext)
        // and creates the engine that posts back to the UI thread. Always marshal
        // to the UI thread first so the captured SynchronizationContext is the
        // real message-loop one instead of a synthetic fallback (review issue #9).
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
            HandleSessionExpiredAsync,
            OnConnectionChanged);
        _engine.Start();

        _clipboardMonitor = new ClipboardMonitor(text => _engine?.SendLocalText(text, UiText.ClipboardSource));
        _clipboardMonitor.Start();
        _serviceRunning = true;
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
        CancelSessionRecovery();
        ResetSessionRecovery();
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
    // tray icon orphaned (review issue #15).
    public async void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        try
        {
            CancelSessionRecovery();
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
            _trayIcon.Dispose();
            _clipboardMonitor?.Dispose();
        }
        base.Dispose(disposing);
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
        // Surface a corrupted settings file instead of silently resetting
        // (review issue #16). Done here so the MainForm is already alive and
        // the status label can actually receive the message.
        if (!string.IsNullOrWhiteSpace(_settingsStore.LoadError))
        {
            PostStatus(UiText.SettingsLoadFailed(_settingsStore.LoadError));
        }

        var data = _settingsStore.Data;

        // 有保存的密码时，重新登录获取新 session（旧 cookie 在重启后通常已过期）
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
                    data.Salt);
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
                // 登录失败时尝试用旧 session 启动（可能仍然有效）
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
            // 没有保存密码但有旧 session，尝试直接启动
            StartService();
        }
        else
        {
            RefreshUi();
        }
    }

    // WebSocket 会话失效：立即返回，避免旧引擎的 ConnectAsync 等待恢复流程；
    // 恢复是否可能、是否重试由 RunSessionRecoveryAsync 在后台完成。
    private Task HandleSessionExpiredAsync()
    {
        _ = RunSessionRecoveryAsync();
        return Task.CompletedTask;
    }

    // 有保存密码时尝试有界恢复；否则停服、清会话并提示重新登录。
    private async Task RunSessionRecoveryAsync()
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _sessionRecoveryCts, cts);
        CancelAndDispose(previous);

        try
        {
            var data = _settingsStore.Data;
            var canRecover = data.SavePassword
                && !string.IsNullOrWhiteSpace(data.SavedPassword);

            if (!canRecover)
            {
                await StopServiceAsync().ConfigureAwait(false);
                _settingsStore.ClearSession();
                _settingsStore.Save();
                PostStatus(UiText.SessionExpiredPleaseLogin);
                RefreshUi();
                return;
            }

            if (Interlocked.CompareExchange(ref _sessionRecoveryAttempted, 1, 0) != 0)
            {
                await StopServiceAsync().ConfigureAwait(false);
                _settingsStore.ClearSession();
                _settingsStore.Save();
                PostStatus(UiText.SessionExpiredPleaseLogin);
                RefreshUi();
                return;
            }

            PostStatus(UiText.SessionRecovering);
            await StopServiceAsync().ConfigureAwait(false);

            for (var attempt = 0; attempt < SessionRecoveryMaxAttempts; attempt++)
            {
                cts.Token.ThrowIfCancellationRequested();
                try
                {
                    var request = new LoginRequest(
                        data.ServerUrl,
                        data.Username,
                        data.SavedPassword,
                        data.HashRounds,
                        data.Salt);
                    await LoginCoreAsync(request, cts.Token).ConfigureAwait(false);
                    PostStatus(UiText.LoginSuccessful);
                    return;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return;
                }
                catch when (attempt + 1 < SessionRecoveryMaxAttempts)
                {
                    await Task.Delay(SessionRecoveryDelays[attempt], cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error)
        {
            PostStatus(UiText.AutoLoginFailed(error.Message));
            _settingsStore.ClearSession();
            _settingsStore.Save();
            RefreshUi();
        }
        finally
        {
            var current = Interlocked.CompareExchange(ref _sessionRecoveryCts, null, cts);
            if (ReferenceEquals(current, cts))
            {
                cts.Dispose();
            }
        }
    }

    private void CancelSessionRecovery()
    {
        var cts = Interlocked.Exchange(ref _sessionRecoveryCts, null);
        CancelAndDispose(cts);
    }

    private static void CancelAndDispose(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
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

    private void ResetSessionRecovery()
    {
        Interlocked.Exchange(ref _sessionRecoveryAttempted, 0);
    }

    // 主窗体不存在时直接丢弃 UI 动作
    private void PostToUi(Action action)
    {
        if (_mainForm is { IsDisposed: false })
        {
            if (_mainForm.InvokeRequired)
            {
                _mainForm.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
    }

    private static bool HasServiceSession(SettingsData data)
    {
        return !string.IsNullOrWhiteSpace(data.CookieHeader)
            && !string.IsNullOrWhiteSpace(data.WebsocketUrl);
    }

    private void PostStatus(string message)
    {
        if (_mainForm is { IsDisposed: false })
        {
            if (_mainForm.InvokeRequired)
            {
                _mainForm.BeginInvoke(() => _mainForm.SetStatus(message));
            }
            else
            {
                _mainForm.SetStatus(message);
            }
        }
    }

    private void RefreshUi()
    {
        if (_mainForm is { IsDisposed: false })
        {
            if (_mainForm.InvokeRequired)
            {
                _mainForm.BeginInvoke(() => _mainForm.RefreshFromState());
            }
            else
            {
                _mainForm.RefreshFromState();
            }
        }
    }
}
