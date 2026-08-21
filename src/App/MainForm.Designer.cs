using System.Drawing;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

public sealed partial class MainForm
{
    // 服务器地址输入框（如 https://host:8443）
    private readonly TextBox _serverUrlBox = new();
    private readonly TextBox _usernameBox = new();
    // 密码框。登录成功后清空，避免明文密码长期驻留内存
    private readonly TextBox _passwordBox = new();
    // 自签证书 SHA-256 指纹输入框（仅在勾选信任所有证书时开放编辑）
    private readonly TextBox _certThumbprintBox = new();
    // PBKDF2 迭代次数。默认 664937，与各端一致
    private readonly NumericUpDown _hashRoundsBox = new();
    // PBKDF2 salt 后缀。可空，与各端约定
    private readonly TextBox _saltBox = new();
    // 本地剪贴板读取上限，避免读入超大文件
    private readonly NumericUpDown _localLimitBox = new();
    // 是否启用 AES-GCM 加密剪贴板内容
    private readonly CheckBox _cipherCheck = new();
    // 是否在本地保存密码（用于重启后自动登录）
    private readonly CheckBox _savePasswordCheck = new();
    // 是否开机自启动
    private readonly CheckBox _startupCheck = new();
    // WebSocket 连接状态变化时是否弹通知
    private readonly CheckBox _statusNotificationCheck = new();
    // 自签部署时是否信任所有证书
    private readonly CheckBox _trustCertCheck = new();
    private readonly Button _loginButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _logoutButton = new();
    private readonly Button _restartButton = new();
    // 状态栏：显示连接/同步/错误等状态消息
    private readonly Label _statusValue = new();
    // 会话状态：已登录/未登录
    private readonly Label _sessionValue = new();
    // WebSocket URL 显示（由服务器地址实时派生）
    private readonly Label _websocketValue = new();
    // 服务状态：运行中/已停止
    private readonly Label _serviceValue = new();

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
        ConfigureNumeric(_localLimitBox, 1, 256L * 1024 * 1024, ClipConfig.DefaultMaxTextBytes);
        AddLabeledControl(securityGrid, UiText.LocalMaxClipboardBytes, _localLimitBox);

        _certThumbprintBox.PlaceholderText = UiText.ThumbprintPlaceholder;
        _certThumbprintBox.Enabled = _trustCertCheck.Checked;
        AddLabeledTextBox(securityGrid, UiText.ServerCertificateThumbprint, _certThumbprintBox);

        ConfigureCheckBox(_cipherCheck, UiText.EnableEncryption);
        ConfigureCheckBox(_savePasswordCheck, UiText.SavePassword);
        ConfigureCheckBox(_startupCheck, UiText.StartWithWindows);
        ConfigureCheckBox(_statusNotificationCheck, UiText.WebSocketStatusNotification);
        ConfigureCheckBox(_trustCertCheck, UiText.TrustAllCertificates);
        _trustCertCheck.CheckedChanged += OnTrustCertCheckChanged;

        var optionsGrid = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Margin = new Padding(0, 4, 0, 0)
        };
        optionsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        optionsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        optionsGrid.Controls.Add(_cipherCheck, 0, 0);
        optionsGrid.Controls.Add(_savePasswordCheck, 1, 0);
        optionsGrid.Controls.Add(_startupCheck, 0, 1);
        optionsGrid.Controls.Add(_statusNotificationCheck, 1, 1);
        optionsGrid.Controls.Add(_trustCertCheck, 0, 2);
        AddWideControl(securityGrid, optionsGrid);
        AddRootControl(root, CreateSection(UiText.SecurityAndLimits, securityGrid));

        var loginRow = CreateButtonRow();
        _loginButton.Text = UiText.Login;
        _saveButton.Text = UiText.Save;
        _logoutButton.Text = UiText.Logout;
        _restartButton.Text = UiText.RestartService;
        ConfigureCommandButton(_loginButton);
        ConfigureCommandButton(_saveButton);
        ConfigureCommandButton(_logoutButton);
        ConfigureCommandButton(_restartButton);
        _loginButton.Click += OnLoginClick;
        _saveButton.Click += OnSaveClick;
        _logoutButton.Click += OnLogoutClick;
        _restartButton.Click += OnRestartClick;
        loginRow.Controls.Add(_loginButton);
        loginRow.Controls.Add(_saveButton);
        loginRow.Controls.Add(_logoutButton);
        loginRow.Controls.Add(_restartButton);
        AddRootControl(root, CreateSection(UiText.Service, loginRow));

        var statusGrid = CreateFormGrid();
        AddStatusRow(statusGrid, UiText.Status, _statusValue);
        AddStatusRow(statusGrid, UiText.Session, _sessionValue);
        AddStatusRow(statusGrid, UiText.WebSocket, _websocketValue);
        AddStatusRow(statusGrid, UiText.Service, _serviceValue);
        AddRootControl(root, CreateSection(UiText.Status, statusGrid));

        // 参数变更不立即保存，需点击"保存"按钮才会持久化并重连。
        _savePasswordCheck.CheckedChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }
            _app.SettingsStore.Data.SavePassword = _savePasswordCheck.Checked;
            if (!_savePasswordCheck.Checked)
            {
                _app.SettingsStore.Data.SavedPassword = string.Empty;
            }
            _app.SaveSettings();
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
