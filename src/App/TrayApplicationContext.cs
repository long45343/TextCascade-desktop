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
        if (!launchedFromStartup)
        {
            ShowMainForm();
        }
    }

    public SettingsStore SettingsStore => _settingsStore;

    public bool ServiceRunning => _serviceRunning;

    public bool IsLoggedIn => HasServiceSession(_settingsStore.Data);

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var data = _settingsStore.Data;
        if (string.IsNullOrWhiteSpace(request.ServerUrl) || string.IsNullOrWhiteSpace(request.Username))
        {
            throw new InvalidOperationException(UiText.RequiredLoginFields);
        }

        var typedPassword = request.Password;
        string passwordSha3;
        string keyBase64;
        if (!string.IsNullOrWhiteSpace(typedPassword))
        {
            passwordSha3 = CryptoManager.Sha3_512LowercaseHex(typedPassword);
            keyBase64 = data.CipherEnabled
                ? Convert.ToBase64String(CryptoManager.DerivePasswordKey(request.Username, typedPassword, request.Salt, request.HashRounds))
                : string.Empty;
        }
        else if (data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPasswordHash))
        {
            if (data.CipherEnabled && string.IsNullOrWhiteSpace(data.HashedPasswordBase64))
            {
                throw new InvalidOperationException(UiText.SavedPasswordEncryptionReuseError);
            }
            passwordSha3 = data.SavedPasswordHash;
            keyBase64 = data.HashedPasswordBase64;
        }
        else
        {
            throw new InvalidOperationException(UiText.RequiredLoginFields);
        }

        var client = new ClipApiClient();
        var result = await client.LoginAsync(
            request.ServerUrl,
            request.Username,
            passwordSha3,
            keyBase64,
            cancellationToken);

        data.ServerUrl = result.NormalizedServerUrl;
        data.Username = request.Username.Trim();
        data.HashRounds = request.HashRounds;
        data.Salt = request.Salt;
        data.PasswordSha3 = passwordSha3;
        data.HashedPasswordBase64 = result.HashedPasswordBase64;
        data.CookieHeader = result.CookieHeader;
        data.WebsocketUrl = result.WebsocketUrl;
        data.CsrfToken = result.CsrfToken;
        data.MaxSizeBytes = result.MaxSizeBytes;
        data.SavedPasswordHash = data.SavePassword ? passwordSha3 : string.Empty;
        _settingsStore.Save();
        StartService();
        return result;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        var data = _settingsStore.Data;
        try
        {
            await new ClipApiClient().LogoutAsync(data.ServerUrl, data.CookieHeader, data.CsrfToken, cancellationToken);
        }
        catch
        {
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
            _ => PostStatus(UiText.RemoteTextApplied));
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

    public void ShowMainForm()
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

        _mainForm.Show();
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

    private void StartServiceAfterMessageLoopStarts(object? sender, EventArgs args)
    {
        Application.Idle -= StartServiceAfterMessageLoopStarts;
        // Surface a corrupted settings file instead of silently resetting
        // (review issue #16). Done here so the MainForm is already alive and
        // the status label can actually receive the message.
        if (!string.IsNullOrWhiteSpace(_settingsStore.LoadError))
        {
            PostStatus(UiText.SettingsLoadFailed(_settingsStore.LoadError));
        }
        if (IsLoggedIn)
        {
            StartService();
        }
        else
        {
            RefreshUi();
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
