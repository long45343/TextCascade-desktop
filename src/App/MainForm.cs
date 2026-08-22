using System.Drawing;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 主配置窗口。提供登录/注销/重启服务按钮，以及各项设置输入框。
// 窗口可被关闭到托盘（不退出进程）；右键托盘图标"显示主窗口"可重新打开。
// 界面控件与布局构建逻辑收敛在 MainForm.Designer.cs 分部类中。
public sealed partial class MainForm : Form
{
    private readonly TrayApplicationContext _app;
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _updating;
    // 本窗口生命周期内是否已弹出过"信任所有证书"风险确认（每窗口一次）
    private bool _trustCertWarningShown;

    public MainForm(TrayApplicationContext app)
    {
        _app = app;
        Text = "TextCascade";
        Icon = AppIcons.App;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(680, 720);
        ClientSize = new Size(680, 720);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        InitializeControls();
        LoadFromSettings();
        RefreshFromState();
    }

    public void SetStatus(string message)
    {
        var text = UiText.FormatStatus(message);
        _statusValue.Text = string.IsNullOrWhiteSpace(text) ? UiText.Idle : text;
    }

    public void RefreshFromState()
    {
        var data = _app.SettingsStore.Data;
        var loggedIn = _app.IsLoggedIn;
        var running = _app.ServiceRunning;
        _sessionValue.Text = loggedIn ? UiText.LoggedIn : UiText.NotLoggedIn;
        _websocketValue.Text = DisplayWebsocketUrl(data.ServerUrl);
        var isUnpinnedCert = data.TrustAllCertificates && string.IsNullOrWhiteSpace(data.ServerCertificateThumbprint);
        _serviceValue.Text = running
            ? (isUnpinnedCert ? UiText.RunningUnpinnedCertWarning : UiText.Running)
            : UiText.Stopped;
        _loginButton.Enabled = !_updating;
        _saveButton.Enabled = !_updating;
        _logoutButton.Enabled = loggedIn && !_updating;
        _restartButton.Enabled = loggedIn && !_updating;
    }

    // WebSocket 入口由 server_url 实时派生（wss://{host}/api/v1/sync），仅用于显示
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 丢弃未保存的表单更改，恢复为已持久化的设置
        LoadFromSettings();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposeCts.Cancel();
            _disposeCts.Dispose();
        }
        base.Dispose(disposing);
    }

    private bool ConfirmUnpinnedCertIfNecessary()
    {
        if (!_trustCertCheck.Checked || !string.IsNullOrWhiteSpace(_certThumbprintBox.Text))
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            UiText.TrustCertConfirmDialogBody,
            UiText.TrustCertWarningTitle,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        return result == DialogResult.Yes;
    }

    private void OnTrustCertCheckChanged(object? sender, EventArgs e)
    {
        _certThumbprintBox.Enabled = _trustCertCheck.Checked;
        if (_updating)
        {
            return;
        }
        if (_trustCertCheck.Checked && !_trustCertWarningShown)
        {
            _trustCertWarningShown = true;
            MessageBox.Show(
                this,
                UiText.TrustCertWarningBody,
                UiText.TrustCertWarningTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    // WinForms Click handlers must be `async void`. Wrap the inner async task
    // with a top-level try/catch so any unhandled exception is surfaced via the
    // status label instead of crashing the process.
    private async void OnLoginClick(object? sender, EventArgs e)
    {
        try
        {
            await LoginAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            SetStatus(UiText.LoginFailed(error.Message));
        }
    }

    private async void OnLogoutClick(object? sender, EventArgs e)
    {
        try
        {
            await LogoutAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            SetStatus(UiText.LogoutFailed(error.Message));
        }
    }

    private async void OnRestartClick(object? sender, EventArgs e)
    {
        try
        {
            await RestartServiceAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            SetStatus(UiText.RestartServiceFailed(error.Message));
        }
    }

    private async void OnSaveClick(object? sender, EventArgs e)
    {
        try
        {
            await SaveAndReconnectAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            SetStatus(UiText.SaveFailed(error.Message));
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
            var request = new LoginRequest(
                _serverUrlBox.Text,
                _usernameBox.Text,
                _passwordBox.Text,
                (int)_hashRoundsBox.Value,
                _saltBox.Text,
                _trustCertCheck.Checked,
                _certThumbprintBox.Text);
            await _app.LoginAsync(request, _disposeCts.Token).ConfigureAwait(true);
            _passwordBox.Clear();
            SetStatus(UiText.LoginSuccessful);
            LoadFromSettings();
        }
        catch (OperationCanceledException)
        {
            // Form is closing
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

    // 保存当前表单参数。如果已登录，停止服务并用新参数重新登录，
    // 使 AES 密钥等基于最新参数重新派生。未登录时仅保存设置。
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
                // 检查是否有可用密码（输入框或已保存的密码），没有则不停止服务
                var hasPassword = !string.IsNullOrWhiteSpace(_passwordBox.Text)
                    || (_app.SettingsStore.Data.SavePassword
                        && !string.IsNullOrWhiteSpace(_app.SettingsStore.Data.SavedPassword));

                if (!hasPassword)
                {
                    // 无密码可用，仅保存设置，保持当前服务运行
                    SetStatus(UiText.SaveSuccessful);
                }
                else
                {
                    // 停止当前服务，使 LoginAsync 中的 StartService 能用新配置启动引擎
                    await _app.StopServiceAsync().ConfigureAwait(true);

                    var request = new LoginRequest(
                        _serverUrlBox.Text,
                        _usernameBox.Text,
                        _passwordBox.Text,
                        (int)_hashRoundsBox.Value,
                        _saltBox.Text,
                        _trustCertCheck.Checked,
                        _certThumbprintBox.Text);
                    await _app.LoginAsync(request, _disposeCts.Token).ConfigureAwait(true);
                    _passwordBox.Clear();
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
            // 窗口关闭中
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
            _passwordBox.Clear();
            LoadFromSettings();
        }
        catch (OperationCanceledException)
        {
            // Form closing
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
            // Form closing
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

    private void LoadFromSettings()
    {
        _updating = true;
        try
        {
            var data = _app.SettingsStore.Data;
            _serverUrlBox.Text = data.ServerUrl;
            _usernameBox.Text = data.Username;
            _passwordBox.PlaceholderText = data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPassword)
                ? UiText.SavedPasswordPlaceholder
                : "";
            _hashRoundsBox.Value = Math.Clamp(data.HashRounds, (int)_hashRoundsBox.Minimum, (int)_hashRoundsBox.Maximum);
            _saltBox.Text = data.Salt;
            _localLimitBox.Value = Math.Clamp(data.LocalMaxClipboardBytes, (long)_localLimitBox.Minimum, (long)_localLimitBox.Maximum);
            _cipherCheck.Checked = data.CipherEnabled;
            _savePasswordCheck.Checked = data.SavePassword;
            _startupCheck.Checked = data.RelaunchOnBoot;
            _statusNotificationCheck.Checked = data.WebsocketStatusNotification;
            _trustCertCheck.Checked = data.TrustAllCertificates;
            _certThumbprintBox.Text = data.ServerCertificateThumbprint;
            _certThumbprintBox.Enabled = _trustCertCheck.Checked;
        }
        finally
        {
            _updating = false;
        }
    }

    private void SaveFormSettings()
    {
        var data = _app.SettingsStore.Data;
        data.ServerUrl = _serverUrlBox.Text;
        data.Username = _usernameBox.Text;
        data.HashRounds = (int)_hashRoundsBox.Value;
        data.Salt = _saltBox.Text;
        data.LocalMaxClipboardBytes = (long)_localLimitBox.Value;
        data.CipherEnabled = _cipherCheck.Checked;
        data.SavePassword = _savePasswordCheck.Checked;
        data.WebsocketStatusNotification = _statusNotificationCheck.Checked;
        data.TrustAllCertificates = _trustCertCheck.Checked;
        data.ServerCertificateThumbprint = _certThumbprintBox.Text;
        if (data.SavePassword && !string.IsNullOrWhiteSpace(_passwordBox.Text))
        {
            data.SavedPassword = _passwordBox.Text;
        }
        _app.SaveSettings();
    }

    private void SetBusy(bool busy)
    {
        _updating = busy;
        _loginButton.Enabled = !busy;
        _saveButton.Enabled = !busy;
        _logoutButton.Enabled = !busy && _app.IsLoggedIn;
        _restartButton.Enabled = !busy && _app.IsLoggedIn;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }
}
