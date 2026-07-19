using System.Drawing;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 主配置窗口。提供登录/注销/重启服务按钮，以及各项设置输入框。
// 窗口可被关闭到托盘（不退出进程）；右键托盘图标"显示主窗口"可重新打开。
// 所有 Click 事件用命名 async void 方法包裹，加顶层 try/catch
// 防止未捕获异常导致进程崩溃（review issue #13）
public sealed class MainForm : Form
{
    private readonly TrayApplicationContext _app;
    // 服务器地址输入框（如 http://localhost:8080）
    private readonly TextBox _serverUrlBox = new();
    private readonly TextBox _usernameBox = new();
    // 密码框。登录成功后清空，避免明文密码长期驻留内存
    private readonly TextBox _passwordBox = new();
    // PBKDF2 迭代次数。默认 664937，与各端一致
    private readonly NumericUpDown _hashRoundsBox = new();
    // PBKDF2 salt 后缀。可空，与各端约定
    private readonly TextBox _saltBox = new();
    // 本地剪贴板读取上限，避免读入超大文件
    private readonly NumericUpDown _localLimitBox = new();
    // 是否启用 AES-GCM 加密剪贴板内容
    private readonly CheckBox _cipherCheck = new();
    // 是否在本地保存密码 hash（用于重启后自动填充用户名）
    private readonly CheckBox _savePasswordCheck = new();
    // 是否开机自启动
    private readonly CheckBox _startupCheck = new();
    // WebSocket 连接状态变化时是否弹通知
    private readonly CheckBox _statusNotificationCheck = new();
    private readonly Button _loginButton = new();
    private readonly Button _logoutButton = new();
    private readonly Button _restartButton = new();
    // 状态栏：显示连接/同步/错误等状态消息
    private readonly Label _statusValue = new();
    // 会话状态：已登录/未登录
    private readonly Label _sessionValue = new();
    // WebSocket URL 显示
    private readonly Label _websocketValue = new();
    // 服务状态：运行中/已停止
    private readonly Label _serviceValue = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _updating;

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
        _statusValue.Text = string.IsNullOrWhiteSpace(message) ? UiText.Idle : message;
    }

    public void RefreshFromState()
    {
        var data = _app.SettingsStore.Data;
        var loggedIn = _app.IsLoggedIn;
        var running = _app.ServiceRunning;
        _sessionValue.Text = loggedIn ? UiText.LoggedIn : UiText.NotLoggedIn;
        _websocketValue.Text = string.IsNullOrWhiteSpace(data.WebsocketUrl) ? UiText.None : data.WebsocketUrl;
        _serviceValue.Text = running ? UiText.Running : UiText.Stopped;
        _loginButton.Enabled = !_updating;
        _logoutButton.Enabled = loggedIn && !_updating;
        _restartButton.Enabled = loggedIn && !_updating;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _app.SaveSettings();
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

    private void InitializeControls()
    {
        SuspendLayout();
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = SystemColors.Control;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 0,
            Padding = new Padding(18, 14, 18, 18),
            AutoScroll = true,
            AutoSize = false
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        AddRootControl(root, CreateHeader());

        var connectionGrid = CreateFormGrid();
        AddLabeledTextBox(connectionGrid, UiText.ServerUrl, _serverUrlBox);
        AddLabeledTextBox(connectionGrid, UiText.Username, _usernameBox);
        AddLabeledTextBox(connectionGrid, UiText.Password, _passwordBox);
        _passwordBox.UseSystemPasswordChar = true;
        AddRootControl(root, CreateSection(UiText.Connection, connectionGrid));

        var securityGrid = CreateFormGrid();
        ConfigureNumeric(_hashRoundsBox, 1, 10_000_000, ClipConfig.DefaultHashRounds);
        AddLabeledControl(securityGrid, UiText.HashRounds, _hashRoundsBox);
        AddLabeledTextBox(securityGrid, UiText.EncryptionSalt, _saltBox);
        ConfigureNumeric(_localLimitBox, 1, 256L * 1024 * 1024, ClipConfig.DefaultMaxSizeBytes);
        AddLabeledControl(securityGrid, UiText.LocalMaxClipboardBytes, _localLimitBox);

        ConfigureCheckBox(_cipherCheck, UiText.EnableEncryption);
        ConfigureCheckBox(_savePasswordCheck, UiText.SavePassword);
        ConfigureCheckBox(_startupCheck, UiText.StartWithWindows);
        ConfigureCheckBox(_statusNotificationCheck, UiText.WebSocketStatusNotification);
        var optionsGrid = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 4, 0, 0)
        };
        optionsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        optionsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        optionsGrid.Controls.Add(_cipherCheck, 0, 0);
        optionsGrid.Controls.Add(_savePasswordCheck, 1, 0);
        optionsGrid.Controls.Add(_startupCheck, 0, 1);
        optionsGrid.Controls.Add(_statusNotificationCheck, 1, 1);
        AddWideControl(securityGrid, optionsGrid);
        AddRootControl(root, CreateSection(UiText.SecurityAndLimits, securityGrid));

        var loginRow = CreateButtonRow();
        _loginButton.Text = UiText.Login;
        _logoutButton.Text = UiText.Logout;
        _restartButton.Text = UiText.RestartService;
        ConfigureCommandButton(_loginButton);
        ConfigureCommandButton(_logoutButton);
        ConfigureCommandButton(_restartButton);
        _loginButton.Click += OnLoginClick;
        _logoutButton.Click += OnLogoutClick;
        _restartButton.Click += OnRestartClick;
        loginRow.Controls.Add(_loginButton);
        loginRow.Controls.Add(_logoutButton);
        loginRow.Controls.Add(_restartButton);
        AddRootControl(root, CreateSection(UiText.Service, loginRow));

        var statusGrid = CreateFormGrid();
        AddStatusRow(statusGrid, UiText.Status, _statusValue);
        AddStatusRow(statusGrid, UiText.Session, _sessionValue);
        AddStatusRow(statusGrid, UiText.WebSocket, _websocketValue);
        AddStatusRow(statusGrid, UiText.Service, _serviceValue);
        AddRootControl(root, CreateSection(UiText.Status, statusGrid));

        _serverUrlBox.TextChanged += (_, _) => SaveFormSettings();
        _usernameBox.TextChanged += (_, _) => SaveFormSettings();
        _saltBox.TextChanged += (_, _) => SaveFormSettings();
        _hashRoundsBox.ValueChanged += (_, _) => SaveFormSettings();
        _localLimitBox.ValueChanged += (_, _) => SaveFormSettings();
        _cipherCheck.CheckedChanged += (_, _) => SaveFormSettings();
        _statusNotificationCheck.CheckedChanged += (_, _) => SaveFormSettings();
        _savePasswordCheck.CheckedChanged += (_, _) =>
        {
            SaveFormSettings();
            if (!_savePasswordCheck.Checked)
            {
                _app.SettingsStore.Data.SavedPasswordHash = string.Empty;
                _app.SaveSettings();
            }
        };
        _startupCheck.CheckedChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }
            try
            {
                _app.SetStartup(_startupCheck.Checked);
            }
            catch (Exception error)
            {
                _startupCheck.Checked = _app.SettingsStore.Data.RelaunchOnBoot;
                SetStatus(UiText.StartupRegistrationFailed(error.Message));
            }
        };
        ResumeLayout(performLayout: true);
    }

    // WinForms Click handlers must be `async void`. Wrap the inner async task
    // with a top-level try/catch so any unhandled exception is surfaced via the
    // status label instead of crashing the process (review issue #13).
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

    private async Task LoginAsync()
    {
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
                _saltBox.Text);
            await _app.LoginAsync(request, _disposeCts.Token).ConfigureAwait(true);
            _passwordBox.Clear();
            SetStatus(UiText.LoginSuccessful);
            LoadFromSettings();
        }
        catch (OperationCanceledException)
        {
            // Form is closing (Dispose cancelled the token). Suppress rather
            // than showing "login failed" while the window is going away
            // (review issue #14).
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
            // Form closing: see LoginAsync (review issue #14).
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
            // Form closing: see LoginAsync (review issue #14).
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
            _passwordBox.PlaceholderText = data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPasswordHash)
                ? UiText.SavedPasswordPlaceholder
                : "";
            _hashRoundsBox.Value = Math.Clamp(data.HashRounds, (int)_hashRoundsBox.Minimum, (int)_hashRoundsBox.Maximum);
            _saltBox.Text = data.Salt;
            _localLimitBox.Value = Math.Clamp(data.LocalMaxClipboardBytes, (long)_localLimitBox.Minimum, (long)_localLimitBox.Maximum);
            _cipherCheck.Checked = data.CipherEnabled;
            _savePasswordCheck.Checked = data.SavePassword;
            _startupCheck.Checked = data.RelaunchOnBoot;
            _statusNotificationCheck.Checked = data.WebsocketStatusNotification;
        }
        finally
        {
            _updating = false;
        }
    }

    private void SaveFormSettings()
    {
        if (_updating)
        {
            return;
        }

        var data = _app.SettingsStore.Data;
        data.ServerUrl = SettingsStore.NormalizeServerUrl(_serverUrlBox.Text);
        data.Username = _usernameBox.Text.Trim();
        data.HashRounds = (int)_hashRoundsBox.Value;
        data.Salt = _saltBox.Text;
        data.LocalMaxClipboardBytes = (long)_localLimitBox.Value;
        data.CipherEnabled = _cipherCheck.Checked;
        data.SavePassword = _savePasswordCheck.Checked;
        data.WebsocketStatusNotification = _statusNotificationCheck.Checked;
        _app.SaveSettings();
        _passwordBox.PlaceholderText = data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPasswordHash)
            ? UiText.SavedPasswordPlaceholder
            : "";
    }

    private void SetBusy(bool busy)
    {
        _updating = busy;
        if (busy)
        {
            _loginButton.Enabled = false;
            _logoutButton.Enabled = false;
            _restartButton.Enabled = false;
        }
        else
        {
            RefreshFromState();
        }
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private static void AddLabeledTextBox(TableLayoutPanel panel, string label, TextBox textBox)
    {
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0, 1, 0, 8);
        AddLabeledControl(panel, label, textBox);
    }

    private static void AddLabeledControl(TableLayoutPanel panel, string label, Control control)
    {
        var row = panel.RowCount;
        panel.RowCount = row + 1;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var labelControl = new Label
        {
            Text = label,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 1, 12, 8)
        };
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 1, 0, 8);
        panel.Controls.Add(labelControl, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private static void AddWideControl(TableLayoutPanel panel, Control control)
    {
        var row = panel.RowCount;
        panel.RowCount = row + 1;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(control, 0, row);
        panel.SetColumnSpan(control, 2);
    }

    private static void AddStatusRow(TableLayoutPanel panel, string label, Label valueLabel)
    {
        valueLabel.AutoSize = false;
        valueLabel.Dock = DockStyle.Fill;
        valueLabel.MinimumSize = new Size(320, 24);
        valueLabel.TextAlign = ContentAlignment.MiddleLeft;
        valueLabel.Margin = new Padding(0, 1, 0, 7);
        valueLabel.AutoEllipsis = true;
        AddLabeledControl(panel, label, valueLabel);
    }

    private static void ConfigureNumeric(NumericUpDown control, decimal min, decimal max, decimal value)
    {
        control.Minimum = min;
        control.Maximum = max;
        control.Value = Math.Clamp(value, min, max);
        control.DecimalPlaces = 0;
        control.ThousandsSeparator = true;
        control.Dock = DockStyle.Fill;
    }

    private static void ConfigureCheckBox(CheckBox checkBox, string text)
    {
        checkBox.Text = text;
        checkBox.AutoSize = false;
        checkBox.Dock = DockStyle.Fill;
        checkBox.Height = 26;
        checkBox.Margin = new Padding(0, 4, 16, 4);
    }

    private static Control CreateHeader()
    {
        var header = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 10)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var icon = new PictureBox
        {
            Image = AppIcons.App.ToBitmap(),
            SizeMode = PictureBoxSizeMode.StretchImage,
            Size = new Size(40, 40),
            Margin = new Padding(0, 0, 12, 0)
        };
        var title = new Label
        {
            Text = "TextCascade",
            AutoSize = true,
            Font = new Font("Segoe UI", 18F, FontStyle.Bold, GraphicsUnit.Point),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 3, 0, 0)
        };
        header.Controls.Add(icon, 0, 0);
        header.Controls.Add(title, 1, 0);
        return header;
    }

    private static GroupBox CreateSection(string title, Control content)
    {
        var group = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(12, 10, 12, 12),
            Margin = new Padding(0, 0, 0, 12)
        };
        content.Dock = DockStyle.Fill;
        group.Controls.Add(content);
        return group;
    }

    private static TableLayoutPanel CreateFormGrid()
    {
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 0,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private static FlowLayoutPanel CreateButtonRow()
    {
        return new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 8),
            WrapContents = false
        };
    }

    private static void ConfigureCommandButton(Button button)
    {
        button.Width = 112;
        button.Height = 28;
        button.Margin = new Padding(0, 0, 10, 0);
    }

    private static void AddRootControl(TableLayoutPanel root, Control control)
    {
        var row = root.RowCount;
        root.RowCount = row + 1;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Top;
        root.Controls.Add(control, 0, row);
    }
}
