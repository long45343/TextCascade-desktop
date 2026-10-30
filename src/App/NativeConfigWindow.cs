using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.App;

// 拥有 Windows 11 现代沉浸式大标题栏（48px Custom Titlebar）+ HTML 紧凑栅格排版的纯原生 Win32 配置窗口。
// 核心升级：
//   - 沉浸式 48px 大标题栏：彻底移除了 Windows 默认局促的 31px 窄白条，左侧嵌入全新黑白 App Logo（24x24）与纯正 TextCascade 标题（删除无意义设置二字）；
//   - 右上角集成自绘 Windows 11 现代矢量最小化（细横线）与关闭按钮（细叉），按客户区宽度绝对贴边对齐；
//   - 卡片大标题 Logo 采用自绘与 DrawText(DT_VCENTER) 共享水平垂直中心线，100% 消除错位；
//   - 卡片 2（安全与限制）采用高度自适应（当未启用 SHA-256 时收缩为 4 行，彻底消灭底部多余空白）；
//   - 所有开关复选框改为自绘纯黑白主题（浅色纯黑底白勾，暗色纯白底黑勾，告别刺眼系统蓝）；
//   - 服务按钮与证书/单位下拉框全面 Fluent 化（自绘圆角、纤细向下箭头、自绘下拉高亮项）；
//   - 弹窗全面 Fluent 化（FluentDialog 模态对话框，彻底淘汰古老 MessageBox）；
//   - 全应用高级黑白主题（浅色深黑底白字，深色纯白底黑字），极简克制；
//   - 100% 纯原生 NativeAOT 零反射、零第三方依赖，秒级冷启。
internal sealed class NativeConfigWindow : IDisposable
{
    private const string WindowClassName = "TextCascade_FluentConfigWndClass_v10";

    // 控件命令 ID 定义
    private const int IdServerUrl = 201;
    private const int IdUsername = 202;
    private const int IdBtnAccount = 203;
    private const int IdPassword = 204;
    private const int IdBtnChangePassword = 205;
    private const int IdSavePasswordCheck = 206;

    private const int IdLocalLimit = 211;
    private const int IdUnitCombo = 212;
    private const int IdCipherCheck = 213;
    private const int IdHashRounds = 214;
    private const int IdSalt = 215;
    private const int IdCertModeCombo = 216;
    private const int IdCertThumbprint = 217;

    private const int IdStartupCheck = 221;
    private const int IdStatusNotificationCheck = 222;
    private const int IdUuEvasionCheck = 223;

    private const int IdBtnServiceMenu = 231;
    private const int IdBtnClose = 232;
    private const int IdBtnSaveAndReconnect = 233;

    private const int IdCaptionMin = 301;
    private const int IdCaptionClose = 302;

    private static readonly ConcurrentDictionary<IntPtr, NativeConfigWindow> Instances = new();
    private static readonly ConcurrentDictionary<IntPtr, (NativeConfigWindow Window, IntPtr OldWndProc, bool IsClose)> CaptionBtnInstances = new();
    private static readonly NativeMethods.WndProc StaticWndProc = WindowProcedure;
    private static readonly NativeMethods.WndProc StaticCaptionBtnProc = CaptionButtonWindowProcedure;
    private static readonly object RegisterLock = new();
    private static bool _classRegistered;

    private readonly TrayHost _app;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly IntPtr _hwnd;

    private bool _isMinHovered;
    // 标题栏状态胶囊的客户区矩形与悬停跟踪
    // 胶囊悬停轮询定时器：每 120ms 检查光标是否落在胶囊内。
    // 比依赖 WM_MOUSEMOVE + TrackMouseEvent 更可靠（窗口未激活或缺消息时也能工作）。
    private const nuint PillPollTimerId = 0x41;
    private const uint PillPollIntervalMs = 120;
    // 问题提示卡片停留时长：够看清两行中文，又不至于挡住窗口
    private const int StatusToastLingerMs = 6000;

    private NativeMethods.RECT _rcStatusPill;
    private bool _isPillHovered;
    // 最近一条状态文案，胶囊悬停卡片会带出来
    private string _lastStatusText = string.Empty;

    private bool _isCloseHovered;
    private int _currentUnitIndex = 0; // 0: KB, 1: MB
    private int _currentCertMode = 0; // 0: 使用系统证书验证, 1: 指定证书 SHA-256 指纹, 2: 信任所有证书

    // 现代字阶
    private IntPtr _fontTitle;
    private IntPtr _fontCardTitle;
    private IntPtr _fontBody;
    private IntPtr _fontCaption;
    // 勾选符号专用字体：略大于复选框，让 MiSans 的 ✓ 字形填满方框
    private IntPtr _fontCheck;

    // 控件句柄
    private IntPtr _hCaptionBtnMin;
    private IntPtr _hCaptionBtnClose;

    private IntPtr _hServerUrlBox;
    private IntPtr _hUsernameBox;
    private IntPtr _hBtnAccount;
    private IntPtr _hPasswordBox;
    private IntPtr _hBtnChangePassword;
    private IntPtr _hSavePasswordCheck;

    private IntPtr _hLocalLimitBox;
    private IntPtr _hUnitCombo;
    private IntPtr _hCipherCheck;
    private IntPtr _hHashRoundsLabel;
    private IntPtr _hHashRoundsBox;
    private IntPtr _hSaltLabel;
    private IntPtr _hSaltBox;
    private IntPtr _hCertModeLabel;
    private IntPtr _hCertModeCombo;
    private IntPtr _hCertThumbprintLabel;
    private IntPtr _hCertThumbprintBox;

    private IntPtr _hStartupCheck;
    private IntPtr _hStatusNotificationCheck;
    private IntPtr _hUuEvasionCheck;

    private IntPtr _hBtnServiceMenu;
    private IntPtr _hBtnClose;
    private IntPtr _hBtnSaveAndReconnect;


    // 静态标签分类跟踪
    private readonly HashSet<IntPtr> _cardLabels = new();
    private readonly HashSet<IntPtr> _allEditControls = new();
    private readonly HashSet<int> _checkedBoxes = new();

    // 输入框控件句柄与外框圆角矩形坐标关联列表
    private readonly List<(IntPtr Hwnd, NativeMethods.RECT Rect)> _editBoxes = new();

    // 界面状态与画刷缓存
    private bool _isDarkMode;
    private bool _updating;
    private bool _isEditingPassword;
    private bool _disposed;
    private string _currentWebsocketUrl = string.Empty;

    // 96dpi 逻辑像素到当前监视器物理像素的换算。字体按 DPI 缩放，布局必须同步缩放，
    // 否则高 DPI 下文字比容器大一倍，标题与标签会被裁切。
    private uint _dpi = 96;
    private double _scale = 1.0;

    private int S(int logical) => (int)Math.Round(logical * _scale);

    // GDI 资源
    private IntPtr _hBrushBg;
    private IntPtr _hBrushCard;
    private IntPtr _hBrushEditBg;
    private IntPtr _hBrushEditDisabledBg;
    private IntPtr _hPenBorder;
    private IntPtr _hPenEditBorder;
    private IntPtr _hPenEditDisabledBorder;

    // 卡片与标题栏按键矩形坐标
    private NativeMethods.RECT _btnMinRect;
    private NativeMethods.RECT _btnCloseRect;
    private NativeMethods.RECT _rcCard1;
    private NativeMethods.RECT _rcCard2;
    private NativeMethods.RECT _rcCard3;

    public NativeConfigWindow(TrayHost app)
    {
        _app = app;
        _isDarkMode = DetectIsDarkMode();
        UpdateThemeBrushes();

        EnsureWindowClassRegistered();

        try
        {
            NativeMethods.SetPreferredAppMode(_isDarkMode ? 2 : 1);
        }
        catch
        {
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            "TextCascade",
            NativeMethods.WS_OVERLAPPED | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_THICKFRAME | NativeMethods.WS_CLIPCHILDREN,
            150, 70, 459, 700,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            Instances[_hwnd] = this;

            // 先按窗口所在监视器的 DPI 定标，再按缩放后的逻辑尺寸重设窗口大小
            _dpi = NativeMethods.GetDpiForWindow(_hwnd);
            if (_dpi == 0)
            {
                _dpi = 96;
            }
            _scale = _dpi / 96.0;
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, S(459), S(700),
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER);

            InitializeFonts();
            ApplyDwmAttributes();
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED);
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
            // 窗口可见期间才值得轮询胶囊悬停
            NativeMethods.SetTimer(_hwnd, PillPollTimerId, PillPollIntervalMs, IntPtr.Zero);
        }
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.KillTimer(_hwnd, PillPollTimerId);
            FluentTooltip.Hide();
            _isPillHovered = false;
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        }
    }

    public void SetStatus(string message)
    {
        // Core 层投递的是 CoreStatus 信封，先映射为本地化文案再判断分类
        PublishStatus(UiText.FormatStatus(message), UiText.IsProblemStatus(message), UiText.IsChatterStatus(message));
    }

    // UI 自身操作失败（保存/注销/重启/表单校验）的反馈入口
    public void SetProblem(string message) => PublishStatus(message, isProblem: true, isChatter: false);

    // 状态统一出口：记录最近一条供胶囊悬停卡片展示，问题类状态额外弹一次短时提示。
    // 窗口不可见时交给托盘气泡，避免用户完全看不到反馈。
    private void PublishStatus(string text, bool isProblem, bool isChatter)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _lastStatusText = text;

        if (isProblem)
        {
            if (NativeMethods.IsWindowVisible(_hwnd))
            {
                ShowStatusToast(text);
            }
            else
            {
                _app.NotifyProblem(text);
            }
            return;
        }

        // 过程态与普通信息只留痕，不打扰：胶囊悬停时可见
        if (isChatter)
        {
            return;
        }
    }

    // 在胶囊下方弹出一张限时提示卡片；连续报错时以最新一条替换旧的。
    // 用户正悬停在胶囊上时不弹：悬停卡片本身会带出"最近"一行，替换它反而会造成状态错乱。
    private void ShowStatusToast(string text)
    {
        if (_isPillHovered)
        {
            return;
        }
        var anchor = GetPillScreenAnchor();
        FluentTooltip.Show(_hwnd, anchor.X, anchor.Y, UiText.Status, new[] { text }, _isDarkMode, StatusToastLingerMs);
    }

    // 胶囊左下角在屏幕坐标系中的位置（提示卡片挂在胶囊下方）
    private NativeMethods.POINT GetPillScreenAnchor()
    {
        NativeMethods.GetWindowRect(_hwnd, out var windowRc);
        NativeMethods.GetClientRect(_hwnd, out var clientRc);
        var frameX = (windowRc.Right - windowRc.Left) - (clientRc.Right - clientRc.Left);
        var frameY = (windowRc.Bottom - windowRc.Top) - (clientRc.Bottom - clientRc.Top);

        return new NativeMethods.POINT
        {
            X = windowRc.Left + frameX + _rcStatusPill.Left,
            Y = windowRc.Top + frameY + _rcStatusPill.Bottom + S(6)
        };
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
        var paused = _app.IsPaused;

        // 标题栏状态胶囊在 OnPaint 中直接读取运行状态绘制，这里只需触发重绘
        _currentWebsocketUrl = DisplayWebsocketUrl(data.ServerUrl);

        NativeMethods.SetWindowText(_hBtnAccount, loggedIn ? UiText.Logout : UiText.Login);
        NativeMethods.EnableWindow(_hBtnAccount, !_updating);
        NativeMethods.EnableWindow(_hBtnSaveAndReconnect, !_updating);

        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
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
        var dpi = _dpi;

        if (_fontTitle != IntPtr.Zero) NativeMethods.DeleteObject(_fontTitle);
        if (_fontCardTitle != IntPtr.Zero) NativeMethods.DeleteObject(_fontCardTitle);
        if (_fontBody != IntPtr.Zero) NativeMethods.DeleteObject(_fontBody);
        if (_fontCaption != IntPtr.Zero) NativeMethods.DeleteObject(_fontCaption);
        if (_fontCheck != IntPtr.Zero) NativeMethods.DeleteObject(_fontCheck);

        // 标题栏大标题：14pt，半粗
        var hTitle = -PtToPixelHeight(14, dpi);
        _fontTitle = NativeMethods.CreateFont(
            hTitle, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        // 卡片标题：12.5pt（较正文放大约 10%），绑定 MiSans Demibold 真粗体
        var hCardTitle = -PtToPixelHeight(12.5, dpi);
        _fontCardTitle = NativeMethods.CreateFont(
            hCardTitle, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        // 控件正文：9.5pt
        var hBody = -PtToPixelHeight(9.5, dpi);
        _fontBody = NativeMethods.CreateFont(
            hBody, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        // 说明文字：8.5pt
        var hCaption = -PtToPixelHeight(8.5, dpi);
        _fontCaption = NativeMethods.CreateFont(
            hCaption, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);

        // 勾选符号：字号略大于正文，让 MiSans 的 ✓ 字形饱满填充复选框方框
        var hCheck = -PtToPixelHeight(10.5, dpi);
        _fontCheck = NativeMethods.CreateFont(
            hCheck, 0, 0, 0, NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.GB2312_CHARSET, 0, 0, NativeMethods.CLEARTYPE_NATURAL_QUALITY, 0, EmbeddedFonts.FontFamilyRegular);
    }

    private static int PtToPixelHeight(double pt, uint dpi)
    {
        return (int)Math.Round(pt * dpi / 72.0);
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
        if (_hBrushBg != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushBg);
        if (_hBrushCard != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushCard);
        if (_hBrushEditBg != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushEditBg);
        if (_hBrushEditDisabledBg != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushEditDisabledBg);
        if (_hPenBorder != IntPtr.Zero) NativeMethods.DeleteObject(_hPenBorder);
        if (_hPenEditBorder != IntPtr.Zero) NativeMethods.DeleteObject(_hPenEditBorder);
        if (_hPenEditDisabledBorder != IntPtr.Zero) NativeMethods.DeleteObject(_hPenEditDisabledBorder);

        var bgCol = _isDarkMode ? 0x00202020U : 0x00F3F3F3U;
        var cardCol = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
        var editBgCol = _isDarkMode ? 0x00222222U : 0x00FFFFFFU;
        var editDisabledBgCol = _isDarkMode ? 0x00262626U : 0x00F4F4F4U;
        var borderCol = _isDarkMode ? 0x00383838U : 0x00DEDEDEU;
        var editBorderCol = _isDarkMode ? 0x00444444U : 0x00D1D1D1U;
        var editDisabledBorderCol = _isDarkMode ? 0x00323232U : 0x00E2E2E2U;

        _hBrushBg = NativeMethods.CreateSolidBrush(bgCol);
        _hBrushCard = NativeMethods.CreateSolidBrush(cardCol);
        _hBrushEditBg = NativeMethods.CreateSolidBrush(editBgCol);
        _hBrushEditDisabledBg = NativeMethods.CreateSolidBrush(editDisabledBgCol);
        _hPenBorder = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, borderCol);
        _hPenEditBorder = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, editBorderCol);
        _hPenEditDisabledBorder = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, editDisabledBorderCol);
    }

    private void CreateChildControls()
    {
        NativeMethods.GetClientRect(_hwnd, out var rcClient);
        var winW = rcClient.Right - rcClient.Left;
        if (winW <= 0) winW = S(459);

        var m = ComputeLayout(winW);
        var cardX = m.CardX;
        var cardW = m.CardW;
        var labelW = m.LabelW;
        var ctrlX = m.CtrlX;
        var ctrlW = m.CtrlW;

        _editBoxes.Clear();
        _allEditControls.Clear();
        _cardLabels.Clear();

        // ---------- 1. 沉浸式大标题栏按键 ----------
        var captionH = m.CaptionH;
        var captionBtnW = m.CaptionBtnW;
        _btnMinRect = new NativeMethods.RECT { Left = winW - captionBtnW * 2, Top = 0, Right = winW - captionBtnW, Bottom = captionH };
        _btnCloseRect = new NativeMethods.RECT { Left = winW - captionBtnW, Top = 0, Right = winW, Bottom = captionH };

        _hCaptionBtnMin = CreateButton(IdCaptionMin, string.Empty, _btnMinRect.Left, 0, captionBtnW, captionH);
        _hCaptionBtnClose = CreateButton(IdCaptionClose, string.Empty, _btnCloseRect.Left, 0, captionBtnW, captionH);
        SubclassCaptionButton(_hCaptionBtnMin, isClose: false);
        SubclassCaptionButton(_hCaptionBtnClose, isClose: true);

        var y = S(56);

        // ---------- 2. 卡片 1：连接与认证 ----------
        var rowH = m.RowH;
        var headerH = m.HeaderH;
        var rowGap = S(5);
        var editH = m.EditH;
        var chkH = m.ChkH;

        var card1Top = y;
        var card1H = headerH + rowH * 3 + S(28) + S(8);
        _rcCard1 = new NativeMethods.RECT { Left = cardX, Top = card1Top, Right = cardX + cardW, Bottom = card1Top + card1H };

        var rowY = card1Top + headerH;
        CreateLabel(UiText.ServerUrl, cardX + S(16), rowY + rowGap, labelW, S(20), _fontBody, isCardInternal: true);
        _hServerUrlBox = CreateRoundedEdit(IdServerUrl, ctrlX, rowY + S(1), ctrlW, editH);
        rowY += rowH;

        CreateLabel(UiText.Username, cardX + S(16), rowY + rowGap, labelW, S(20), _fontBody, isCardInternal: true);
        var btnW = S(76);
        var btnGap = S(8);
        var userEditW = ctrlW - btnW - btnGap;
        _hUsernameBox = CreateRoundedEdit(IdUsername, ctrlX, rowY + S(1), userEditW, editH);
        _hBtnAccount = CreateButton(IdBtnAccount, UiText.Logout, ctrlX + userEditW + btnGap, rowY + S(1), btnW, editH);
        rowY += rowH;

        CreateLabel(UiText.Password, cardX + S(16), rowY + rowGap, labelW, S(20), _fontBody, isCardInternal: true);
        var pwdEditW = ctrlW - btnW - btnGap;
        _hPasswordBox = CreateRoundedEdit(IdPassword, ctrlX, rowY + S(1), pwdEditW, editH, isPassword: true);
        _hBtnChangePassword = CreateButton(IdBtnChangePassword, "更换", ctrlX + pwdEditW + btnGap, rowY + S(1), btnW, editH);
        rowY += rowH;

        _hSavePasswordCheck = CreateCheckBox(IdSavePasswordCheck, UiText.SavePassword, ctrlX, rowY + S(3), S(160), chkH);

        y = _rcCard1.Bottom + S(12);

        // ---------- 3. 卡片 2：安全与限制 ----------
        var card2Top = y;
        var card2H = headerH + rowH * 4 + S(8); // 默认自适应为 4 行
        _rcCard2 = new NativeMethods.RECT { Left = cardX, Top = card2Top, Right = cardX + cardW, Bottom = card2Top + card2H };

        rowY = card2Top + headerH;
        CreateLabel(UiText.LocalMaxClipboardBytes, cardX + S(16), rowY + rowGap, labelW, S(20), _fontBody, isCardInternal: true);
        var limitW = S(95);
        var unitW = S(72);
        _hLocalLimitBox = CreateRoundedEdit(IdLocalLimit, ctrlX, rowY + S(1), limitW, editH);
        _hUnitCombo = CreateButton(IdUnitCombo, string.Empty, ctrlX + limitW + S(8), rowY + S(1), unitW, editH);
        rowY += rowH;

        CreateLabel("内容加密", cardX + S(16), rowY + rowGap, labelW, S(20), _fontBody, isCardInternal: true);
        _hCipherCheck = CreateCheckBox(IdCipherCheck, UiText.EnableEncryption, ctrlX, rowY + S(3), S(140), chkH);
        rowY += rowH;

        var roundsW = S(65);
        var saltLabelW = S(48);
        var saltGap = S(8);
        _hHashRoundsLabel = CreateLabel(UiText.HashRounds, cardX + S(16), rowY + rowGap, labelW, S(20), _fontBody, isCardInternal: true);
        _hHashRoundsBox = CreateRoundedEdit(IdHashRounds, ctrlX, rowY + S(1), roundsW, editH);
        _hSaltLabel = CreateLabel(UiText.EncryptionSalt, ctrlX + roundsW + saltGap, rowY + rowGap, saltLabelW, S(20), _fontBody, isCardInternal: true);
        var saltX = ctrlX + roundsW + saltGap + saltLabelW + saltGap;
        var saltW = ctrlX + ctrlW - saltX;
        _hSaltBox = CreateRoundedEdit(IdSalt, saltX, rowY + S(1), saltW, editH);
        rowY += rowH;

        _hCertModeLabel = CreateLabel("证书校验", cardX + S(16), rowY + rowGap, labelW, S(20), _fontBody, isCardInternal: true);
        _hCertModeCombo = CreateButton(IdCertModeCombo, string.Empty, ctrlX, rowY + S(1), ctrlW, editH);
        rowY += rowH;

        _hCertThumbprintLabel = CreateLabel(UiText.ServerCertificateThumbprint, cardX + S(16), rowY + rowGap, labelW, S(20), _fontBody, isCardInternal: true);
        _hCertThumbprintBox = CreateRoundedEdit(IdCertThumbprint, ctrlX, rowY + S(1), ctrlW, editH);

        y = _rcCard2.Bottom + S(12);

        // ---------- 4. 卡片 3：选项（单行横排） ----------
        var card3Top = y;
        var card3RowH = S(32);
        var card3H = headerH + card3RowH + S(8);
        _rcCard3 = new NativeMethods.RECT { Left = cardX, Top = card3Top, Right = cardX + cardW, Bottom = card3Top + card3H };

        var chkY = card3Top + headerH + S(4);
        var chk1W = S(95);
        var chk2W = S(105);
        var chk3W = S(125);
        var chkGap = S(8);
        var chk1X = cardX + S(14);
        var chk2X = chk1X + chk1W + chkGap;
        var chk3X = chk2X + chk2W + chkGap;
        _hStartupCheck = CreateCheckBox(IdStartupCheck, UiText.StartWithWindows, chk1X, chkY, chk1W, chkH);
        _hStatusNotificationCheck = CreateCheckBox(IdStatusNotificationCheck, UiText.WebSocketStatusNotification, chk2X, chkY, chk2W, chkH);
        _hUuEvasionCheck = CreateCheckBox(IdUuEvasionCheck, UiText.UuEvasion, chk3X, chkY, chk3W, chkH);

        y = _rcCard3.Bottom + S(14);

        // ---------- 5. 操作栏 ----------
        var opH = S(32);
        var serviceW = S(84);
        _hBtnServiceMenu = CreateButton(IdBtnServiceMenu, "服务", cardX, y, serviceW, opH);

        var saveBtnW = S(120);
        var closeBtnW = S(68);
        var saveBtnX = cardX + cardW - saveBtnW;
        var closeBtnX = saveBtnX - closeBtnW - S(8);

        _hBtnClose = CreateButton(IdBtnClose, "关闭", closeBtnX, y, closeBtnW, opH);
        _hBtnSaveAndReconnect = CreateButton(IdBtnSaveAndReconnect, UiText.SaveAndReconnect, saveBtnX, y, saveBtnW, opH);

        // 状态改为标题栏右侧胶囊，窗口高度随之收紧到内容所需
        ResizeWindowToContent(y + opH + S(16));
    }

    // 布局几何的唯一来源：创建与重排必须使用同一套数值，否则会出现"创建时一个坐标、
    // 重排后另一个坐标"的漂移（指纹输入框的内外框错位就是这样产生的）。
    private readonly record struct LayoutMetrics(
        int WinW, int CardX, int CardW, int LabelW, int CtrlX, int CtrlW,
        int RowH, int HeaderH, int EditH, int ChkH, int CaptionH, int CaptionBtnW)
    {
        public static LayoutMetrics Compute(int winW, Func<int, int> s) => new(
            winW,
            s(14), winW - s(28), s(100),
            s(14) + s(16) + s(100) + s(8),
            winW - s(28) - s(14) - (s(14) + s(16) + s(100) + s(8)),
            s(36), s(36), s(28), s(24), s(48), s(46));
    }

    // 按当前客户区宽度算出布局几何
    private LayoutMetrics ComputeLayout(int winW) => LayoutMetrics.Compute(winW, S);

    private void RelayoutControls()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.GetClientRect(_hwnd, out var rcClient);
        var winW = rcClient.Right - rcClient.Left;
        if (winW <= 0) winW = S(459);

        var m = ComputeLayout(winW);
        var cardX = m.CardX;
        var cardW = m.CardW;
        var labelW = m.LabelW;
        var ctrlX = m.CtrlX;
        var ctrlW = m.CtrlW;
        var rowH = m.RowH;
        var headerH = m.HeaderH;
        var editH = m.EditH;
        var chkH = m.ChkH;

        // 重新停靠右上角大标题栏按钮
        var captionH = m.CaptionH;
        var captionBtnW = m.CaptionBtnW;
        _btnMinRect = new NativeMethods.RECT { Left = winW - captionBtnW * 2, Top = 0, Right = winW - captionBtnW, Bottom = captionH };
        _btnCloseRect = new NativeMethods.RECT { Left = winW - captionBtnW, Top = 0, Right = winW, Bottom = captionH };
        MoveChild(_hCaptionBtnMin, _btnMinRect.Left, 0, captionBtnW, captionH);
        MoveChild(_hCaptionBtnClose, _btnCloseRect.Left, 0, captionBtnW, captionH);

        var fingerprintActive = _currentCertMode == 1;

        // 卡片 2 高度自适应：未选指纹时 4 行，选指纹时 5 行
        var card2Rows = fingerprintActive ? 5 : 4;
        var card2H = headerH + rowH * card2Rows + S(8);
        _rcCard2 = new NativeMethods.RECT { Left = cardX, Top = _rcCard1.Bottom + S(12), Right = cardX + cardW, Bottom = _rcCard1.Bottom + S(12) + card2H };

        NativeMethods.ShowWindow(_hCertThumbprintLabel, fingerprintActive ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        NativeMethods.ShowWindow(_hCertThumbprintBox, fingerprintActive ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);

        if (fingerprintActive)
        {
            var fpRowY = _rcCard2.Top + headerH + rowH * 4;
            MoveChild(_hCertThumbprintLabel, cardX + S(16), fpRowY + S(5), labelW, S(20));
            // Edit 控件实际位置由 CreateRoundedEdit 的内缩约定决定（左右各留 S(8)、上下各留 S(4)），
            // 重排时必须用同一约定，否则内框与外框会错位
            MoveChild(_hCertThumbprintBox, ctrlX + S(8), fpRowY + S(1) + S(4), ctrlW - S(16), editH - S(8));
            NativeMethods.EnableWindow(_hCertThumbprintBox, true);
            NativeMethods.InvalidateRect(_hCertThumbprintBox, IntPtr.Zero, true);

            // 圆角外框列表按外框矩形记录（与创建时一致）
            for (var i = 0; i < _editBoxes.Count; i++)
            {
                if (_editBoxes[i].Hwnd == _hCertThumbprintBox)
                {
                    _editBoxes[i] = (_hCertThumbprintBox, new NativeMethods.RECT
                    {
                        Left = ctrlX,
                        Top = fpRowY + S(1),
                        Right = ctrlX + ctrlW,
                        Bottom = fpRowY + S(1) + editH
                    });
                    break;
                }
            }
        }
        else
        {
            NativeMethods.EnableWindow(_hCertThumbprintBox, false);
        }

        // 卡片 3 紧随卡片 2 移动
        var card3Top = _rcCard2.Bottom + S(12);
        var card3H = headerH + S(32) + S(8);
        _rcCard3 = new NativeMethods.RECT { Left = cardX, Top = card3Top, Right = cardX + cardW, Bottom = card3Top + card3H };

        var chkY = card3Top + headerH + S(4);
        var chk1W = S(95);
        var chk2W = S(105);
        var chk3W = S(125);
        var chkGap = S(8);
        var chk1X = cardX + S(14);
        var chk2X = chk1X + chk1W + chkGap;
        var chk3X = chk2X + chk2W + chkGap;
        MoveChild(_hStartupCheck, chk1X, chkY, chk1W, chkH);
        MoveChild(_hStatusNotificationCheck, chk2X, chkY, chk2W, chkH);
        MoveChild(_hUuEvasionCheck, chk3X, chkY, chk3W, chkH);

        // 操作栏按钮
        var opY = _rcCard3.Bottom + S(16);
        var opH = S(32);
        MoveChild(_hBtnServiceMenu, cardX, opY, S(84), opH);

        var saveBtnW = S(120);
        var closeBtnW = S(68);
        var saveBtnX = cardX + cardW - saveBtnW;
        var closeBtnX = saveBtnX - closeBtnW - S(8);

        MoveChild(_hBtnClose, closeBtnX, opY, closeBtnW, opH);
        MoveChild(_hBtnSaveAndReconnect, saveBtnX, opY, saveBtnW, opH);

        // 状态改为标题栏胶囊，窗口高度按内容自适应
        ResizeWindowToContent(opY + opH + S(16));

        // 整窗连同全部子控件立即重绘，清除控件移位后残留的旧像素
        RepaintAll();
    }

    // 按内容底部决定窗口高度，避免删除状态卡片后下方留出空白
    private void ResizeWindowToContent(int contentBottom)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }
        NativeMethods.GetWindowRect(_hwnd, out var windowRc);
        NativeMethods.GetClientRect(_hwnd, out var clientRc);
        var nonClientH = (windowRc.Bottom - windowRc.Top) - (clientRc.Bottom - clientRc.Top);
        var wantClientH = contentBottom;
        var wantTotalH = wantClientH + Math.Max(0, nonClientH);
        var currentH = windowRc.Bottom - windowRc.Top;
        if (Math.Abs(wantTotalH - currentH) > 1)
        {
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0,
                windowRc.Right - windowRc.Left, wantTotalH,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOCOPYBITS);
        }
    }

    // 移动子控件时带 SWP_NOCOPYBITS：默认行为会把控件旧位置的像素拷贝到新位置，
    // 表现为切换证书模式后按钮/输入框在旧坐标留下黑条与灰框。
    private static void MoveChild(IntPtr hChild, int x, int y, int w, int h)
    {
        if (hChild == IntPtr.Zero)
        {
            return;
        }
        NativeMethods.SetWindowPos(hChild, IntPtr.Zero, x, y, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOCOPYBITS);
    }

    private void RepaintAll()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }
        NativeMethods.RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_ERASE | NativeMethods.RDW_ALLCHILDREN | NativeMethods.RDW_UPDATENOW);
    }

    private IntPtr CreateLabel(string text, int x, int y, int w, int h, IntPtr hFont, bool isCardInternal)
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

        if (isCardInternal)
        {
            _cardLabels.Add(hwnd);
        }
        return hwnd;
    }

    private IntPtr CreateRoundedEdit(int id, int x, int y, int w, int h, bool isPassword = false)
    {
        var style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.ES_AUTOHSCROLL;
        if (isPassword)
        {
            style |= NativeMethods.ES_PASSWORD;
        }

        var hwnd = NativeMethods.CreateWindowEx(
            0,
            "EDIT",
            string.Empty,
            style,
            x + S(8), y + S(4), w - S(16), h - S(8),
            _hwnd,
            (IntPtr)id,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_fontBody, 1);

        _editBoxes.Add((hwnd, new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }));
        _allEditControls.Add(hwnd);
        return hwnd;
    }

    // 下拉弹层锚点：紧贴按钮下沿，宽度与按钮一致。返回物理屏幕像素（与 CreateWindowEx 一致）
    private static bool TryGetPopupAnchor(IntPtr hControl, out int left, out int top, out int width)
    {
        left = 0;
        top = 0;
        width = 0;
        if (hControl == IntPtr.Zero || !NativeMethods.GetWindowRect(hControl, out var rc))
        {
            return false;
        }
        left = rc.Left;
        top = rc.Bottom + 2;
        width = rc.Right - rc.Left;
        return true;
    }

    private void ShowUnitMenu()
    {
        var labels = new[] { "KB", "MB" };
        if (!TryGetPopupAnchor(_hUnitCombo, out var left, out var top, out var width))
        {
            return;
        }

        var picked = FluentPopupMenu.Show(_hwnd, left, top, width, labels, _currentUnitIndex, _isDarkMode);
        if (picked >= 0 && picked != _currentUnitIndex)
        {
            _currentUnitIndex = picked;
            NativeMethods.InvalidateRect(_hUnitCombo, IntPtr.Zero, true);
        }
    }

    private void ShowCertModeMenu()
    {
        var labels = new[] { "使用系统证书验证", "指定证书 SHA-256 指纹", "信任所有证书" };
        if (!TryGetPopupAnchor(_hCertModeCombo, out var left, out var top, out var width))
        {
            return;
        }

        var picked = FluentPopupMenu.Show(_hwnd, left, top, width, labels, _currentCertMode, _isDarkMode);
        if (picked < 0)
        {
            return;
        }

        if (picked == 2)
        {
            var confirmed = FluentDialog.ShowConfirm(
                _hwnd,
                UiText.TrustCertWarningTitle,
                UiText.TrustCertConfirmDialogBody,
                _isDarkMode);
            if (!confirmed)
            {
                return;
            }
        }

        _currentCertMode = picked;
        NativeMethods.InvalidateRect(_hCertModeCombo, IntPtr.Zero, true);
        UpdateFieldsEnableState();
    }

    private void SubclassCaptionButton(IntPtr hBtn, bool isClose)
    {
        var oldProc = NativeMethods.SetWindowLongPtr(hBtn, NativeMethods.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(StaticCaptionBtnProc));
        CaptionBtnInstances[hBtn] = (this, oldProc, isClose);
    }

    private static nint CaptionButtonWindowProcedure(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
    {
        if (CaptionBtnInstances.TryGetValue(hWnd, out var item))
        {
            return item.Window.HandleCaptionBtnMessage(hWnd, msg, wParam, lParam, item.OldWndProc, item.IsClose);
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private nint HandleCaptionBtnMessage(IntPtr hWnd, uint msg, nuint wParam, nint lParam, IntPtr oldWndProc, bool isClose)
    {
        switch (msg)
        {
            case NativeMethods.WM_MOUSEMOVE:
                var wasHovered = isClose ? _isCloseHovered : _isMinHovered;
                if (!wasHovered)
                {
                    if (isClose) _isCloseHovered = true;
                    else _isMinHovered = true;

                    var tme = new NativeMethods.TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                        dwFlags = NativeMethods.TME_LEAVE,
                        hwndTrack = hWnd,
                        dwHoverTime = 0
                    };
                    NativeMethods.TrackMouseEvent(ref tme);
                    NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                }
                break;

            case NativeMethods.WM_MOUSELEAVE:
                if (isClose) _isCloseHovered = false;
                else _isMinHovered = false;
                NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                break;

            case 0x0082: // WM_NCDESTROY
                CaptionBtnInstances.TryRemove(hWnd, out _);
                break;
        }

        return NativeMethods.CallWindowProc(oldWndProc, hWnd, msg, wParam, lParam);
    }

    private IntPtr CreateCheckBox(int id, string text, int x, int y, int w, int h)
    {
        var hwnd = NativeMethods.CreateWindowEx(
            0,
            "BUTTON",
            text,
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.BS_OWNERDRAW,
            x, y, w, h,
            _hwnd,
            (IntPtr)id,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETFONT, (nuint)_fontBody, 1);
        return hwnd;
    }

    // 外观完全由 OnDrawItem 按 CtlID 分派决定，创建时无需外观参数
    private IntPtr CreateButton(int id, string text, int x, int y, int w, int h)
    {
        var style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_TABSTOP | NativeMethods.BS_OWNERDRAW;

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

    private void SetCheck(int id, bool isChecked)
    {
        if (isChecked)
        {
            _checkedBoxes.Add(id);
        }
        else
        {
            _checkedBoxes.Remove(id);
        }
        var hCtrl = GetHwndById(id);
        if (hCtrl != IntPtr.Zero)
        {
            NativeMethods.InvalidateRect(hCtrl, IntPtr.Zero, true);
        }
    }

    private bool IsChecked(int id) => _checkedBoxes.Contains(id);

    private IntPtr GetHwndById(int id) => id switch
    {
        IdSavePasswordCheck => _hSavePasswordCheck,
        IdCipherCheck => _hCipherCheck,
        IdStartupCheck => _hStartupCheck,
        IdStatusNotificationCheck => _hStatusNotificationCheck,
        IdUuEvasionCheck => _hUuEvasionCheck,
        _ => IntPtr.Zero
    };

    private void LoadFromSettings()
    {
        _updating = true;
        try
        {
            var data = _app.SettingsStore.Data;
            NativeMethods.SetWindowText(_hServerUrlBox, data.ServerUrl ?? string.Empty);
            NativeMethods.SetWindowText(_hUsernameBox, data.Username ?? string.Empty);

            _isEditingPassword = false;
            ApplyPasswordUiState(string.Empty);

            NativeMethods.SetWindowText(_hHashRoundsBox, data.HashRounds.ToString());
            NativeMethods.SetWindowText(_hSaltBox, data.Salt ?? string.Empty);

            var bytes = data.LocalMaxClipboardBytes;
            if (bytes > 0 && bytes % (1024 * 1024) == 0 && bytes >= (1024 * 1024))
            {
                NativeMethods.SetWindowText(_hLocalLimitBox, (bytes / (1024 * 1024)).ToString());
                _currentUnitIndex = 1;
            }
            else
            {
                var kb = Math.Max(1, bytes / 1024);
                NativeMethods.SetWindowText(_hLocalLimitBox, kb.ToString());
                _currentUnitIndex = 0;
            }

            if (data.TrustAllCertificates)
            {
                _currentCertMode = 2;
            }
            else if (!string.IsNullOrWhiteSpace(data.ServerCertificateThumbprint))
            {
                _currentCertMode = 1;
            }
            else
            {
                _currentCertMode = 0;
            }

            NativeMethods.SetWindowText(_hCertThumbprintBox, data.ServerCertificateThumbprint ?? string.Empty);

            SetCheck(IdCipherCheck, data.CipherEnabled);
            SetCheck(IdSavePasswordCheck, data.SavePassword);
            SetCheck(IdStartupCheck, data.RelaunchOnBoot);
            SetCheck(IdStatusNotificationCheck, data.WebsocketStatusNotification);
            SetCheck(IdUuEvasionCheck, data.UuEvasionEnabled);

            UpdateFieldsEnableState();
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

        var limitStr = GetControlText(_hLocalLimitBox);
        if (long.TryParse(limitStr, out var limitVal) && limitVal > 0)
        {
            var multiplier = _currentUnitIndex == 1 ? (1024L * 1024L) : 1024L;
            data.LocalMaxClipboardBytes = limitVal * multiplier;
        }

        switch (_currentCertMode)
        {
            case 2:
                data.TrustAllCertificates = true;
                data.ServerCertificateThumbprint = string.Empty;
                break;
            case 1:
                data.TrustAllCertificates = false;
                data.ServerCertificateThumbprint = GetControlText(_hCertThumbprintBox);
                break;
            default:
                data.TrustAllCertificates = false;
                data.ServerCertificateThumbprint = string.Empty;
                break;
        }

        data.CipherEnabled = IsChecked(IdCipherCheck);
        data.SavePassword = IsChecked(IdSavePasswordCheck);
        data.WebsocketStatusNotification = IsChecked(IdStatusNotificationCheck);
        data.UuEvasionEnabled = IsChecked(IdUuEvasionCheck);

        // 开机启动直接写 HKCU Run 项。以注册表真实状态为准回写内存字段，
        // 避免写入失败时 UI 与系统长期漂移。
        var wantStartup = IsChecked(IdStartupCheck);
        if (wantStartup != StartupManager.IsEnabled())
        {
            try
            {
                StartupManager.SetEnabled(wantStartup);
            }
            catch (Exception error)
            {
                SetProblem(UiText.StartupRegistrationFailed(error.Message));
            }
        }
        data.RelaunchOnBoot = StartupManager.IsEnabled();

        if (_isEditingPassword)
        {
            var typedPassword = GetControlText(_hPasswordBox);
            if (!string.IsNullOrWhiteSpace(typedPassword))
            {
                data.SavedPassword = typedPassword;
            }
        }
        _app.SaveSettings();
    }

    // 密码区三态的唯一渲染点：已保存待复用 / 正在输入 / 无保存密码。
    // 返回是否已保存密码，供调用方判断后续登录流程。
    private bool ApplyPasswordUiState(string typedPassword)
    {
        var data = _app.SettingsStore.Data;
        var hasSaved = data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPassword);

        if (_isEditingPassword)
        {
            NativeMethods.SendMessageString(_hPasswordBox, NativeMethods.EM_SETCUEBANNER, 0, string.Empty);
            NativeMethods.SetWindowText(_hPasswordBox, typedPassword);
            NativeMethods.SetWindowText(_hBtnChangePassword, UiText.CancelEditing);
            return hasSaved;
        }

        NativeMethods.SendMessageString(_hPasswordBox, NativeMethods.EM_SETCUEBANNER, 0,
            hasSaved ? UiText.SavedPasswordPlaceholder : string.Empty);
        NativeMethods.SetWindowText(_hPasswordBox, string.Empty);
        NativeMethods.SetWindowText(_hBtnChangePassword, hasSaved ? UiText.ChangePassword : UiText.EnterPassword);
        return hasSaved;
    }

    private void UpdateFieldsEnableState()
    {
        var cipherOn = IsChecked(IdCipherCheck);
        NativeMethods.EnableWindow(_hHashRoundsBox, cipherOn);
        NativeMethods.EnableWindow(_hSaltBox, cipherOn);

        RelayoutControls();
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
                    hCursor = NativeMethods.LoadCursor(IntPtr.Zero, (nint)32512),
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
            case 0x0083: // WM_NCCALCSIZE
                if (wParam != 0)
                {
                    // 消除系统 31px 默认窄白条非客户区，使 48px 大标题栏完全沉浸式展示
                    return 0;
                }
                return 0;

            case 0x0084: // WM_NCHITTEST
                var screenX = (short)(lParam & 0xFFFF);
                var screenY = (short)((lParam >> 16) & 0xFFFF);
                var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
                NativeMethods.ScreenToClient(_hwnd, ref pt);

                // 顶部大标题栏拖动支持。高度必须按 DPI 缩放：写死 48 会让 150% 缩放下
                // 72px 标题栏的下半截拖不动，200% 下可用拖动区不足三分之一。
                if (pt.Y >= 0 && pt.Y <= S(48))
                {
                    // 避开右上角最小化与关闭按钮
                    if (pt.X >= _btnMinRect.Left && pt.X <= _btnMinRect.Right && pt.Y >= _btnMinRect.Top && pt.Y <= _btnMinRect.Bottom)
                    {
                        return 1; // HTCLIENT
                    }
                    if (pt.X >= _btnCloseRect.Left && pt.X <= _btnCloseRect.Right && pt.Y >= _btnCloseRect.Top && pt.Y <= _btnCloseRect.Bottom)
                    {
                        return 1; // HTCLIENT
                    }
                    return 2; // HTCAPTION (允许按住自由拖拽移动窗口)
                }
                return 1; // HTCLIENT (防止 WS_THICKFRAME 边框被误判为调整大小)

            case 0x00A3: // WM_NCLBUTTONDBLCLK
                return 0; // 阻止双击最大化

            case NativeMethods.WM_PAINT:
                OnPaint();
                return 0;

            case NativeMethods.WM_CTLCOLORSTATIC:
            case NativeMethods.WM_CTLCOLORBTN:
                return OnCtlColorStatic(wParam, lParam);

            case NativeMethods.WM_CTLCOLOREDIT:
                return OnCtlColorEdit(wParam, lParam);

            case NativeMethods.WM_CTLCOLORLISTBOX:
                var hdcList = (IntPtr)wParam;
                var listBg = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
                var listText = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
                NativeMethods.SetBkColor(hdcList, listBg);
                NativeMethods.SetBkMode(hdcList, NativeMethods.OPAQUE);
                NativeMethods.SetTextColor(hdcList, listText);
                return _hBrushCard;

            case NativeMethods.WM_DRAWITEM:
                OnDrawItem(lParam);
                return 1;

            case NativeMethods.WM_TIMER:
                if (wParam == PillPollTimerId)
                {
                    PollPillHover();
                    return 0;
                }
                break;

            case NativeMethods.WM_SETTINGCHANGE:
                OnSettingChange();
                return 0;

            // 跨监视器拖动导致 DPI 变化：用系统建议的新矩形重建字体、重设窗口并重新布局
            case NativeMethods.WM_DPICHANGED:
                OnDpiChanged(wParam, lParam);
                return 0;

            case NativeMethods.WM_CLOSE:
                LoadFromSettings();
                Hide();
                return 0;

            case NativeMethods.WM_COMMAND:
                var ctrlId = (int)(wParam & 0xFFFF);
                var code = (int)((wParam >> 16) & 0xFFFF);
                OnCommand(ctrlId, code);
                return 0;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private unsafe void OnPaint()
    {
        var hdc = NativeMethods.BeginPaint(_hwnd, out var ps);
        try
        {
            NativeMethods.FillRect(hdc, in ps.rcPaint, _hBrushBg);

            // 1. 绘制沉浸式大标题栏（左侧 App Logo + TextCascade 纯标题）
            var titleBarH = S(48);
            var logoSize = S(24);
            // 标题栏 Logo 也按实际像素尺寸取图，避免高 DPI 下放大产生锯齿
            NativeMethods.DrawIconEx(hdc, S(16), (titleBarH - logoSize) / 2, AppIcons.AppIcon(logoSize), logoSize, logoSize, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);

            var titleText = "TextCascade";
            var textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;

            NativeMethods.SetBkColor(hdc, _isDarkMode ? 0x00202020U : 0x00F3F3F3U);
            NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);

            var prevFont = NativeMethods.SelectObject(hdc, _fontTitle);
            NativeMethods.SetTextColor(hdc, textCol);
            // 标题区域按实际文本宽度 + 余量取，右侧留给胶囊，避免被挤压截断
            var titleLeft = S(48);
            var titleWidth = MeasureTextWidth(hdc, _fontTitle, titleText) + S(8);
            var titleRight = titleLeft + titleWidth;
            var titleRc = new NativeMethods.RECT { Left = titleLeft, Top = 0, Right = titleRight, Bottom = titleBarH };
            NativeMethods.DrawText(hdc, titleText, titleText.Length, ref titleRc, NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE | NativeMethods.DT_NOPREFIX);
            NativeMethods.SelectObject(hdc, prevFont);

            // 2. 绘制三大卡片
            DrawCard(hdc, in _rcCard1);
            DrawCard(hdc, in _rcCard2);
            DrawCard(hdc, in _rcCard3);

            // 3. 绘制三大卡片大标题 Logo 与标题文本（严格同轴垂直居中对齐，消除错位）
            DrawCardHeader(hdc, _rcCard1, UiText.Connection, AppIcons.ConnectionIcon(S(20)));
            DrawCardHeader(hdc, _rcCard2, UiText.SecurityAndLimits, AppIcons.SecurityIcon(S(20)));
            DrawCardHeader(hdc, _rcCard3, UiText.Options, AppIcons.OptionsIcon(S(20)));

            // 4. 绘制输入框圆角矩形外框
            foreach (var (hEdit, rc) in _editBoxes)
            {
                // 指纹输入框仅在「指定证书 SHA-256 指纹」模式下可见，隐藏态不绘制外框
                if (hEdit == _hCertThumbprintBox && _currentCertMode != 1)
                {
                    continue;
                }

                var isEnabled = NativeMethods.IsWindowEnabled(hEdit);
                var brush = isEnabled ? _hBrushEditBg : _hBrushEditDisabledBg;
                var pen = isEnabled ? _hPenEditBorder : _hPenEditDisabledBorder;

                var prevBrush = NativeMethods.SelectObject(hdc, brush);
                var prevPen = NativeMethods.SelectObject(hdc, pen);
                var radius = S(8);
                NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, radius, radius);
                NativeMethods.SelectObject(hdc, prevBrush);
                NativeMethods.SelectObject(hdc, prevPen);
            }

            // 5. 绘制紧贴标题右侧的状态胶囊
            DrawStatusPills(hdc, titleBarH, titleRight + S(12));
        }
        finally
        {
            NativeMethods.EndPaint(_hwnd, in ps);
        }
    }

    private void DrawCardHeader(IntPtr hdc, in NativeMethods.RECT rcCard, string text, IntPtr hIcon)
    {
        var headerH = S(36);
        var cardTop = rcCard.Top;
        var centerY = cardTop + headerH / 2;

        var iconSize = S(20);
        var iconX = rcCard.Left + S(16);
        var iconY = centerY - iconSize / 2;
        NativeMethods.DrawIconEx(hdc, iconX, iconY, hIcon, iconSize, iconSize, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);

        // 2. 标题文字垂直几何居中
        var cardCol = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
        var textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
        NativeMethods.SetBkColor(hdc, cardCol);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, textCol);

        var prevFont = NativeMethods.SelectObject(hdc, _fontCardTitle);
        var textRc = new NativeMethods.RECT
        {
            Left = iconX + iconSize + S(8),
            Top = cardTop,
            Right = rcCard.Right - S(16),
            Bottom = cardTop + headerH
        };
        NativeMethods.DrawText(hdc, text, text.Length, ref textRc, NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        NativeMethods.SelectObject(hdc, prevFont);
    }

    // 标题栏右侧状态胶囊：会话 + 服务，各自带彩色指示灯，替代原先底部的整条状态卡
    // 紧贴标题右侧的单一状态胶囊：同时体现会话与服务状态。
    // 定位锚点是标题文本右边界（而非窗口右边缘），窗口加宽时胶囊始终跟着标题走。
    private void DrawStatusPills(IntPtr hdc, int titleBarH, int anchorLeft)
    {
        var loggedIn = _app.IsLoggedIn;
        var running = _app.ServiceRunning;
        var paused = _app.IsPaused;

        var serviceState = running ? (paused ? "已暂停" : UiText.Running) : UiText.Stopped;
        var text = $"{UiText.Service}: {serviceState}";
        // 圆点取服务状态优先、其次会话状态：服务在跑就是绿的
        var dotColor = running
            ? (paused ? 0x00013BD8U : 0x00107C41U)
            : (loggedIn ? 0x00107C41U : 0x00808080U);

        var pillH = S(24);
        var pillY = (titleBarH - pillH) / 2;
        var padX = S(10);
        var dotSize = S(7);
        var textGap = S(6);

        // 紧贴标题右侧；若会碰到窗口按钮则回退，保证不与最小化/关闭重叠
        var pillW = MeasurePillWidth(hdc, text, padX, dotSize, textGap);
        var pillLeft = anchorLeft;
        var rightLimit = _btnMinRect.Left - S(8);
        if (pillLeft + pillW > rightLimit)
        {
            pillLeft = Math.Max(S(8), rightLimit - pillW);
        }

        // 记录胶囊矩形，供鼠标悬停检测使用
        _rcStatusPill = new NativeMethods.RECT
        {
            Left = pillLeft,
            Top = pillY,
            Right = pillLeft + pillW,
            Bottom = pillY + pillH
        };

        DrawPill(hdc, pillLeft, pillY, pillW, pillH, text, dotColor, padX, dotSize, textGap);
    }

    private int MeasureTextWidth(IntPtr hdc, IntPtr hFont, string text)
    {
        var prev = NativeMethods.SelectObject(hdc, hFont);
        var size = new NativeMethods.SIZE();
        var ok = NativeMethods.GetTextExtentPoint32(hdc, text, text.Length, out size);
        NativeMethods.SelectObject(hdc, prev);
        return ok ? size.cx : text.Length * S(9);
    }

    private int MeasurePillWidth(IntPtr hdc, string text, int padX, int dotSize, int textGap)
    {
        var prevFont = NativeMethods.SelectObject(hdc, _fontCaption);
        var size = new NativeMethods.SIZE();
        if (!NativeMethods.GetTextExtentPoint32(hdc, text, text.Length, out size))
        {
            size.cx = text.Length * S(8);
        }
        NativeMethods.SelectObject(hdc, prevFont);
        return padX * 2 + dotSize + textGap + size.cx;
    }

    private void DrawPill(IntPtr hdc, int left, int top, int width, int height, string text, uint dotColor, int padX, int dotSize, int textGap)
    {
        // 胶囊底：浅色描边圆角条，深浅色各一套
        var pillBg = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
        var pillBorder = _isDarkMode ? 0x00404040U : 0x00E0E0E0U;

        var hBrush = NativeMethods.CreateSolidBrush(pillBg);
        var hPen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, 1, pillBorder);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
        var prevPen = NativeMethods.SelectObject(hdc, hPen);
        NativeMethods.RoundRect(hdc, left, top, left + width, top + height, height, height);
        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);
        NativeMethods.DeleteObject(hPen);

        // 状态指示圆点
        var dotLeft = left + padX;
        var dotTop = top + (height - dotSize) / 2;
        var hDotBrush = NativeMethods.CreateSolidBrush(dotColor);
        var prevDotBrush = NativeMethods.SelectObject(hdc, hDotBrush);
        NativeMethods.SelectObject(hdc, NativeMethods.GetStockObject(NativeMethods.NULL_PEN));
        NativeMethods.Ellipse(hdc, dotLeft, dotTop, dotLeft + dotSize, dotTop + dotSize);
        NativeMethods.SelectObject(hdc, prevDotBrush);
        NativeMethods.DeleteObject(hDotBrush);

        // 文字
        var textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
        NativeMethods.SetBkColor(hdc, pillBg);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, textCol);

        var prevFont = NativeMethods.SelectObject(hdc, _fontCaption);
        var textRc = new NativeMethods.RECT
        {
            Left = dotLeft + dotSize + textGap,
            Top = top,
            Right = left + width - padX,
            Bottom = top + height
        };
        NativeMethods.DrawText(hdc, text, text.Length, ref textRc,
            NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        NativeMethods.SelectObject(hdc, prevFont);
    }

    private void DrawCard(IntPtr hdc, in NativeMethods.RECT rc)
    {
        var prevBrush = NativeMethods.SelectObject(hdc, _hBrushCard);
        var prevPen = NativeMethods.SelectObject(hdc, _hPenBorder);

        var radius = S(16);
        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, radius, radius);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
    }

    private nint OnCtlColorStatic(nuint wParam, nint lParam)
    {
        var hdc = (IntPtr)wParam;
        var hwndCtrl = (IntPtr)lParam;

        if (_allEditControls.Contains(hwndCtrl))
        {
            var isEnabled = NativeMethods.IsWindowEnabled(hwndCtrl);
            var editBg = isEnabled
                ? (_isDarkMode ? 0x00222222U : 0x00FFFFFFU)
                : (_isDarkMode ? 0x00262626U : 0x00F4F4F4U);
            var editText = isEnabled
                ? (_isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU)
                : (_isDarkMode ? 0x00777777U : 0x008E8E8EU);

            NativeMethods.SetBkColor(hdc, editBg);
            NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
            NativeMethods.SetTextColor(hdc, editText);
            return isEnabled ? _hBrushEditBg : _hBrushEditDisabledBg;
        }

        if (_cardLabels.Contains(hwndCtrl))
        {
            var cardBg = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
            var textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;

            NativeMethods.SetBkColor(hdc, cardBg);
            NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
            NativeMethods.SetTextColor(hdc, textCol);
            return _hBrushCard;
        }

        var outerBg = _isDarkMode ? 0x00202020U : 0x00F3F3F3U;
        var outerTextCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
        NativeMethods.SetBkColor(hdc, outerBg);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, outerTextCol);
        return _hBrushBg;
    }

    private nint OnCtlColorEdit(nuint wParam, nint lParam)
    {
        var hdc = (IntPtr)wParam;
        var hwndCtrl = (IntPtr)lParam;
        var isEnabled = NativeMethods.IsWindowEnabled(hwndCtrl);
        var editBg = isEnabled
            ? (_isDarkMode ? 0x00222222U : 0x00FFFFFFU)
            : (_isDarkMode ? 0x00262626U : 0x00F4F4F4U);
        var editText = isEnabled
            ? (_isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU)
            : (_isDarkMode ? 0x00777777U : 0x008E8E8EU);

        NativeMethods.SetBkColor(hdc, editBg);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, editText);
        return isEnabled ? _hBrushEditBg : _hBrushEditDisabledBg;
    }

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

        // 1. 标题栏按钮（最小化 / 关闭 - 现代矢量细线绘制）
        if (pDis->CtlID == IdCaptionMin || pDis->CtlID == IdCaptionClose)
        {
            DrawCaptionButton(pDis);
            return;
        }

        // 3. 所有复选框（纯正黑白 Fluent 主题 CheckBox）
        if (pDis->CtlID == IdSavePasswordCheck ||
            pDis->CtlID == IdCipherCheck ||
            pDis->CtlID == IdStartupCheck ||
            pDis->CtlID == IdStatusNotificationCheck ||
            pDis->CtlID == IdUuEvasionCheck)
        {
            DrawFluentCheckBox(pDis);
            return;
        }

        // 4. 所有下拉选择框（单位、证书校验、服务，100% 统一为「服务」同款 Fluent 极简按钮风格）
        if (pDis->CtlID == IdUnitCombo)
        {
            DrawDropdownButton(pDis, _currentUnitIndex == 1 ? "MB" : "KB");
            return;
        }
        if (pDis->CtlID == IdCertModeCombo)
        {
            var certText = _currentCertMode switch
            {
                1 => "指定证书 SHA-256 指纹",
                2 => "信任所有证书",
                _ => "使用系统证书验证"
            };
            DrawDropdownButton(pDis, certText);
            return;
        }
        if (pDis->CtlID == IdBtnServiceMenu)
        {
            DrawDropdownButton(pDis, "服务");
            return;
        }

        // 5. 保存并重连（黑白主题 Accent 主按钮）
        if (pDis->CtlID == IdBtnSaveAndReconnect)
        {
            DrawPrimarySaveButton(pDis);
            return;
        }

        // 6. 其他标准按钮（注销、更换、关闭）
        DrawSecondaryButton(pDis);
    }

    private unsafe void DrawCaptionButton(NativeMethods.DRAWITEMSTRUCT* pDis)
    {
        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isSelected = (pDis->itemState & NativeMethods.ODS_SELECTED) != 0;
        var isClose = pDis->CtlID == IdCaptionClose;

        uint capBg;
        uint capFg;

        if (isClose)
        {
            if (isSelected)
            {
                capBg = 0x001C2BC4U;
                capFg = 0x00FFFFFFU;
            }
            else if (_isCloseHovered)
            {
                capBg = 0x002311E8U;
                capFg = 0x00FFFFFFU;
            }
            else
            {
                capBg = _isDarkMode ? 0x00202020U : 0x00F3F3F3U;
                capFg = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            }
        }
        else
        {
            if (isSelected)
            {
                capBg = _isDarkMode ? 0x00404040U : 0x00CCCCCCU;
                capFg = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            }
            else if (_isMinHovered)
            {
                capBg = _isDarkMode ? 0x00333333U : 0x00E5E5E5U;
                capFg = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            }
            else
            {
                capBg = _isDarkMode ? 0x00202020U : 0x00F3F3F3U;
                capFg = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            }
        }

        var hCapBrush = NativeMethods.CreateSolidBrush(capBg);
        NativeMethods.FillRect(hdc, in rc, hCapBrush);
        NativeMethods.DeleteObject(hCapBrush);

        var centerX = (rc.Left + rc.Right) / 2;
        var centerY = (rc.Top + rc.Bottom) / 2;

        var hPen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, Math.Max(1, S(1)), capFg);
        var prevPen = NativeMethods.SelectObject(hdc, hPen);
        var arm = S(5);

        if (isClose)
        {
            // 交叉现代细线
            NativeMethods.MoveToEx(hdc, centerX - arm, centerY - arm, IntPtr.Zero);
            NativeMethods.LineTo(hdc, centerX + arm, centerY + arm);
            NativeMethods.MoveToEx(hdc, centerX - arm, centerY + arm, IntPtr.Zero);
            NativeMethods.LineTo(hdc, centerX + arm, centerY - arm);
        }
        else
        {
            // 水平现代极简细线
            NativeMethods.MoveToEx(hdc, centerX - arm, centerY, IntPtr.Zero);
            NativeMethods.LineTo(hdc, centerX + arm, centerY);
        }

        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hPen);
    }

    // 在复选框里居中绘制 MiSans 的 ✓ 字形（U+2713）。
    // 用真实字体轮廓替代 GDI 折线：笔画带粗细变化、端点圆润，且有 ClearType 次像素平滑。
    private void DrawCheckGlyph(IntPtr hdc, int boxLeft, int boxTop, int boxSize, uint color)
    {
        const string glyph = "✓";
        var font = _fontCheck != IntPtr.Zero ? _fontCheck : _fontBody;

        var prevFont = NativeMethods.SelectObject(hdc, font);
        NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);
        NativeMethods.SetTextColor(hdc, color);

        var rc = new NativeMethods.RECT
        {
            Left = boxLeft,
            Top = boxTop,
            Right = boxLeft + boxSize,
            Bottom = boxTop + boxSize
        };
        NativeMethods.DrawText(hdc, glyph, glyph.Length, ref rc,
            NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);

        NativeMethods.SelectObject(hdc, prevFont);
    }

    private unsafe void DrawFluentCheckBox(NativeMethods.DRAWITEMSTRUCT* pDis)
    {
        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isChecked = IsChecked((int)pDis->CtlID);

        // 填充控件背板底色
        NativeMethods.FillRect(hdc, in rc, _hBrushCard);

        var boxSize = S(18);
        var boxLeft = rc.Left;
        var boxTop = rc.Top + (rc.Bottom - rc.Top - boxSize) / 2;
        var boxRight = boxLeft + boxSize;
        var boxBottom = boxTop + boxSize;
        var boxRadius = S(4);

        if (isChecked)
        {
            // 纯黑白主题选中状态：浅色纯黑底白勾，暗色纯白底黑勾
            var boxCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
            var checkCol = _isDarkMode ? 0x001A1A1AU : 0x00FFFFFFU;

            var hBrush = NativeMethods.CreateSolidBrush(boxCol);
            var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
            var hPen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, Math.Max(1, S(1)), boxCol);
            var prevPen = NativeMethods.SelectObject(hdc, hPen);

            NativeMethods.RoundRect(hdc, boxLeft, boxTop, boxRight, boxBottom, boxRadius, boxRadius);

            NativeMethods.SelectObject(hdc, prevBrush);
            NativeMethods.SelectObject(hdc, prevPen);
            NativeMethods.DeleteObject(hBrush);
            NativeMethods.DeleteObject(hPen);

            // 绘制对勾：使用 MiSans 的 ✓ 字形（U+2713），与界面字体同源，
            // 比 GDI 折线更饱满平滑、边缘带次像素抗锯齿
            DrawCheckGlyph(hdc, boxLeft, boxTop, boxSize, checkCol);
        }
        else
        {
            // 未选中状态：白色/深色背景，灰色边框
            var boxBg = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;
            var borderCol = 0x008A8A8AU;

            var hBrush = NativeMethods.CreateSolidBrush(boxBg);
            var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
            var hPen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, Math.Max(1, S(1)), borderCol);
            var prevPen = NativeMethods.SelectObject(hdc, hPen);

            NativeMethods.RoundRect(hdc, boxLeft, boxTop, boxRight, boxBottom, boxRadius, boxRadius);

            NativeMethods.SelectObject(hdc, prevBrush);
            NativeMethods.SelectObject(hdc, prevPen);
            NativeMethods.DeleteObject(hBrush);
            NativeMethods.DeleteObject(hPen);
        }

        // 绘制复选框文字
        var text = GetControlText(pDis->hwndItem);
        var textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;
        var cardCol = _isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU;

        NativeMethods.SetBkColor(hdc, cardCol);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, textCol);

        var prevFont = NativeMethods.SelectObject(hdc, _fontBody);
        var textRc = new NativeMethods.RECT
        {
            Left = boxRight + S(8),
            Top = rc.Top,
            Right = rc.Right,
            Bottom = rc.Bottom
        };
        NativeMethods.DrawText(hdc, text, text.Length, ref textRc, NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        NativeMethods.SelectObject(hdc, prevFont);
    }

    private unsafe void DrawDropdownButton(NativeMethods.DRAWITEMSTRUCT* pDis, string text)
    {
        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isSelected = (pDis->itemState & NativeMethods.ODS_SELECTED) != 0;

        uint btnCol = isSelected
            ? (_isDarkMode ? 0x00404040U : 0x00E5E5E5U)
            : (_isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU);
        uint textCol = _isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU;

        var hBrush = NativeMethods.CreateSolidBrush(btnCol);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
        var prevPen = NativeMethods.SelectObject(hdc, _hPenEditBorder);

        var radius = S(8);
        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, radius, radius);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);

        NativeMethods.SetBkColor(hdc, btnCol);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, textCol);

        var prevFont = NativeMethods.SelectObject(hdc, _fontBody);
        var drawRc = new NativeMethods.RECT { Left = rc.Left + S(10), Top = rc.Top, Right = rc.Right - S(22), Bottom = rc.Bottom };
        NativeMethods.DrawText(hdc, text, text.Length, ref drawRc, NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        NativeMethods.SelectObject(hdc, prevFont);

        // 向下纤细折线小箭头 ⌵
        var arrowCol = _isDarkMode ? 0x00CCCCCCU : 0x00666666U;
        var arrowPen = NativeMethods.CreatePen(NativeMethods.PS_SOLID, Math.Max(1, S(1)), arrowCol);
        var prevArrowPen = NativeMethods.SelectObject(hdc, arrowPen);
        var midX = rc.Right - S(13);
        var midY = (rc.Top + rc.Bottom) / 2;
        var arrowW = S(4);
        var arrowH = S(2);

        NativeMethods.MoveToEx(hdc, midX - arrowW, midY - arrowH, IntPtr.Zero);
        NativeMethods.LineTo(hdc, midX, midY + arrowH);
        NativeMethods.LineTo(hdc, midX + arrowW, midY - arrowH);

        NativeMethods.SelectObject(hdc, prevArrowPen);
        NativeMethods.DeleteObject(arrowPen);
    }

    private unsafe void DrawPrimarySaveButton(NativeMethods.DRAWITEMSTRUCT* pDis)
    {
        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isSelected = (pDis->itemState & NativeMethods.ODS_SELECTED) != 0;
        var isDisabled = (pDis->itemState & NativeMethods.ODS_DISABLED) != 0;

        uint btnCol;
        uint textCol;

        if (isDisabled)
        {
            btnCol = _isDarkMode ? 0x00333333U : 0x00E5E5E5U;
            textCol = _isDarkMode ? 0x00777777U : 0x008E8E8EU;
        }
        else if (_isDarkMode)
        {
            btnCol = isSelected ? 0x00CCCCCCU : 0x00FFFFFFU;
            textCol = 0x001A1A1AU;
        }
        else
        {
            btnCol = isSelected ? 0x00333333U : 0x001A1A1AU;
            textCol = 0x00FFFFFFU;
        }

        var hBrush = NativeMethods.CreateSolidBrush(btnCol);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
        var prevPen = NativeMethods.SelectObject(hdc, _hPenBorder);

        var radius = S(8);
        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, radius, radius);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);

        var btnText = UiText.SaveAndReconnect;
        NativeMethods.SetBkColor(hdc, btnCol);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, textCol);

        var prevFont = NativeMethods.SelectObject(hdc, _fontBody);
        var drawRc = rc;
        NativeMethods.DrawText(hdc, btnText, btnText.Length, ref drawRc,
            NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        NativeMethods.SelectObject(hdc, prevFont);
    }

    private unsafe void DrawSecondaryButton(NativeMethods.DRAWITEMSTRUCT* pDis)
    {
        var hdc = pDis->hDC;
        var rc = pDis->rcItem;
        var isSelected = (pDis->itemState & NativeMethods.ODS_SELECTED) != 0;
        var isDisabled = (pDis->itemState & NativeMethods.ODS_DISABLED) != 0;

        uint btnCol = isDisabled
            ? (_isDarkMode ? 0x00262626U : 0x00F4F4F4U)
            : (isSelected
                ? (_isDarkMode ? 0x00404040U : 0x00E5E5E5U)
                : (_isDarkMode ? 0x002C2C2CU : 0x00FFFFFFU));
        uint textCol = isDisabled
            ? (_isDarkMode ? 0x00777777U : 0x008E8E8EU)
            : (_isDarkMode ? 0x00FFFFFFU : 0x001A1A1AU);

        var hBrush = NativeMethods.CreateSolidBrush(btnCol);
        var prevBrush = NativeMethods.SelectObject(hdc, hBrush);
        var prevPen = NativeMethods.SelectObject(hdc, _hPenBorder);

        var radius = S(8);
        NativeMethods.RoundRect(hdc, rc.Left, rc.Top, rc.Right, rc.Bottom, radius, radius);

        NativeMethods.SelectObject(hdc, prevBrush);
        NativeMethods.SelectObject(hdc, prevPen);
        NativeMethods.DeleteObject(hBrush);

        var btnText = GetControlText(pDis->hwndItem);
        NativeMethods.SetBkColor(hdc, btnCol);
        NativeMethods.SetBkMode(hdc, NativeMethods.OPAQUE);
        NativeMethods.SetTextColor(hdc, textCol);

        var prevFont = NativeMethods.SelectObject(hdc, _fontBody);
        var drawRc = rc;
        NativeMethods.DrawText(hdc, btnText, btnText.Length, ref drawRc,
            NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
        NativeMethods.SelectObject(hdc, prevFont);
    }

    // 轮询光标是否落在胶囊内：进入即弹出详情卡片，离开后延时收起
    private void PollPillHover()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(_hwnd))
        {
            return;
        }

        var inside = IsCursorInsidePill();
        if (inside && !_isPillHovered)
        {
            _isPillHovered = true;
            ShowStatusTooltip();
        }
        else if (!inside && _isPillHovered)
        {
            _isPillHovered = false;
            FluentTooltip.ScheduleHide();
        }
    }

    // 用光标实际位置判断是否仍在胶囊内（WM_MOUSELEAVE 不携带坐标）
    private bool IsCursorInsidePill()
    {
        var rc = _rcStatusPill;
        if (rc.Right <= rc.Left)
        {
            return false;
        }
        if (!NativeMethods.GetCursorPos(out var screenPt) || !NativeMethods.ScreenToClient(_hwnd, ref screenPt))
        {
            return false;
        }
        return screenPt.X >= rc.Left && screenPt.X < rc.Right && screenPt.Y >= rc.Top && screenPt.Y < rc.Bottom;
    }

    // 组装并显示连接状态卡片（不含剪贴板内容，避免敏感信息外泄）
    private void ShowStatusTooltip()
    {
        var loggedIn = _app.IsLoggedIn;
        var running = _app.ServiceRunning;
        var paused = _app.IsPaused;

        var serviceLine = $"服务：{(running ? (paused ? "运行中（已暂停）" : "运行中") : "已停止")}";
        var sessionLine = $"登录：{(loggedIn ? "已登录" : "未登录")}";

        var lines = string.IsNullOrWhiteSpace(_lastStatusText)
            ? new[] { serviceLine, sessionLine }
            : new[] { serviceLine, sessionLine, $"{UiText.LastStatus}：{_lastStatusText}" };

        var anchor = GetPillScreenAnchor();
        FluentTooltip.Show(_hwnd, anchor.X, anchor.Y, "连接状态", lines, _isDarkMode);
    }

    private unsafe void OnSettingChange()
    {
        var newDarkMode = DetectIsDarkMode();
        if (newDarkMode != _isDarkMode)
        {
            _isDarkMode = newDarkMode;
            ApplyDwmAttributes();
            UpdateThemeBrushes();
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
        }
    }

    // DPI 变化：更新缩放系数、按系统建议矩形重设窗口、重建字体并重建全部子控件。
    // 必须整体重建而非仅重排：_rcCard1、_editBoxes 里记录的圆角外框坐标、各按钮字体
    // 都在创建时一次性写入，仅调用 RelayoutControls 会漏掉这些状态，导致 UI 撕裂。
    private unsafe void OnDpiChanged(nuint wParam, nint lParam)
    {
        var newDpi = (uint)(wParam & 0xFFFF);
        if (newDpi == 0)
        {
            return;
        }

        // 重排期间冻结绘制，避免新旧尺寸交替的中间态被画到屏幕上
        NativeMethods.SendMessage(_hwnd, NativeMethods.WM_SETREDRAW, 0, 0);
        try
        {
            _dpi = newDpi;
            _scale = _dpi / 96.0;

            // lParam 指向系统建议的新窗口矩形（已按新 DPI 换算）
            var suggested = (NativeMethods.RECT*)lParam;
            if (suggested != null)
            {
                NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero,
                    suggested->Left, suggested->Top,
                    suggested->Right - suggested->Left, suggested->Bottom - suggested->Top,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOCOPYBITS);
            }

            InitializeFonts();

            // 重建会销毁全部子控件，必须先把用户当前输入读出来，重建后原样写回，
            // 否则 DPI 切换会清空尚未保存的编辑内容。
            var certMode = _currentCertMode;
            var unitIndex = _currentUnitIndex;
            var editingPassword = _isEditingPassword;
            var typedServerUrl = GetControlText(_hServerUrlBox);
            var typedUsername = GetControlText(_hUsernameBox);
            var typedPassword = GetControlText(_hPasswordBox);
            var typedLimit = GetControlText(_hLocalLimitBox);
            var typedRounds = GetControlText(_hHashRoundsBox);
            var typedSalt = GetControlText(_hSaltBox);
            var typedThumbprint = GetControlText(_hCertThumbprintBox);

            DestroyChildControls();
            CreateChildControls();

            // 恢复交互状态与用户输入
            _currentCertMode = certMode;
            _currentUnitIndex = unitIndex;
            _isEditingPassword = editingPassword;

            NativeMethods.SetWindowText(_hServerUrlBox, typedServerUrl);
            NativeMethods.SetWindowText(_hUsernameBox, typedUsername);
            NativeMethods.SetWindowText(_hLocalLimitBox, typedLimit);
            NativeMethods.SetWindowText(_hHashRoundsBox, typedRounds);
            NativeMethods.SetWindowText(_hSaltBox, typedSalt);
            NativeMethods.SetWindowText(_hCertThumbprintBox, typedThumbprint);

            // 密码框的 cue banner 与"更换/取消"按钮文案不在 SetWindowText 的覆盖范围内，
            // 必须显式重建，否则跨屏改 DPI 后会变成空提示、空按钮
            ApplyPasswordUiState(typedPassword);

            RefreshFromState();
            UpdateFieldsEnableState();
        }
        finally
        {
            NativeMethods.SendMessage(_hwnd, NativeMethods.WM_SETREDRAW, 1, 0);
            RepaintAll();
        }
    }

    // 销毁现有全部子控件（重建布局前调用）
    private void DestroyChildControls()
    {
        foreach (var (hwnd, _) in _editBoxes)
        {
            DestroyChild(hwnd);
        }
        _editBoxes.Clear();
        _allEditControls.Clear();

        foreach (var hLabel in _cardLabels)
        {
            DestroyChild(hLabel);
        }
        _cardLabels.Clear();

        DestroyChild(_hCaptionBtnMin);
        DestroyChild(_hCaptionBtnClose);
        DestroyChild(_hServerUrlBox);
        DestroyChild(_hUsernameBox);
        DestroyChild(_hBtnAccount);
        DestroyChild(_hPasswordBox);
        DestroyChild(_hBtnChangePassword);
        DestroyChild(_hSavePasswordCheck);
        DestroyChild(_hLocalLimitBox);
        DestroyChild(_hUnitCombo);
        DestroyChild(_hCipherCheck);
        DestroyChild(_hHashRoundsLabel);
        DestroyChild(_hHashRoundsBox);
        DestroyChild(_hSaltLabel);
        DestroyChild(_hSaltBox);
        DestroyChild(_hCertModeLabel);
        DestroyChild(_hCertModeCombo);
        DestroyChild(_hCertThumbprintLabel);
        DestroyChild(_hCertThumbprintBox);
        DestroyChild(_hStartupCheck);
        DestroyChild(_hStatusNotificationCheck);
        DestroyChild(_hUuEvasionCheck);
        DestroyChild(_hBtnServiceMenu);
        DestroyChild(_hBtnClose);
        DestroyChild(_hBtnSaveAndReconnect);

        // 全部置零，避免重建前的悬挂句柄被误用
        _hCaptionBtnMin = _hCaptionBtnClose = IntPtr.Zero;
        _hServerUrlBox = _hUsernameBox = _hBtnAccount = IntPtr.Zero;
        _hPasswordBox = _hBtnChangePassword = _hSavePasswordCheck = IntPtr.Zero;
        _hLocalLimitBox = _hUnitCombo = _hCipherCheck = IntPtr.Zero;
        _hHashRoundsLabel = _hHashRoundsBox = _hSaltLabel = _hSaltBox = IntPtr.Zero;
        _hCertModeLabel = _hCertModeCombo = IntPtr.Zero;
        _hCertThumbprintLabel = _hCertThumbprintBox = IntPtr.Zero;
        _hStartupCheck = _hStatusNotificationCheck = _hUuEvasionCheck = IntPtr.Zero;
        _hBtnServiceMenu = _hBtnClose = _hBtnSaveAndReconnect = IntPtr.Zero;
    }

    private static void DestroyChild(IntPtr hChild)
    {
        if (hChild != IntPtr.Zero && NativeMethods.IsWindow(hChild))
        {
            NativeMethods.DestroyWindow(hChild);
        }
    }

    private void OnCommand(int ctrlId, int code)
    {
        if (_updating)
        {
            return;
        }

        switch (ctrlId)
        {
            case IdCaptionMin:
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWMINIMIZED);
                break;

            case IdCaptionClose:
            case IdBtnClose:
                LoadFromSettings();
                Hide();
                break;

            case IdBtnSaveAndReconnect:
                _ = SaveAndReconnectAsync();
                break;

            case IdBtnAccount:
                if (_app.IsLoggedIn)
                {
                    _ = LogoutAsync();
                }
                else
                {
                    _ = SaveAndReconnectAsync();
                }
                break;

            case IdBtnChangePassword:
                TogglePasswordEditing();
                break;

            case IdBtnServiceMenu:
                ShowServiceMenu();
                break;

            case IdSavePasswordCheck:
            case IdStartupCheck:
            case IdStatusNotificationCheck:
            case IdUuEvasionCheck:
                SetCheck(ctrlId, !IsChecked(ctrlId));
                break;

            case IdCipherCheck:
                SetCheck(ctrlId, !IsChecked(ctrlId));
                UpdateFieldsEnableState();
                break;

            case IdUnitCombo:
                ShowUnitMenu();
                break;

            case IdCertModeCombo:
                ShowCertModeMenu();
                break;
        }
    }

    private void TogglePasswordEditing()
    {
        if (_isEditingPassword)
        {
            // 取消编辑：丢弃已输入内容并回到保存态
            _isEditingPassword = false;
            LoadFromSettings();
            return;
        }

        _isEditingPassword = true;
        ApplyPasswordUiState(string.Empty);
    }

    private void ShowServiceMenu()
    {
        var loggedIn = _app.IsLoggedIn;
        var running = _app.ServiceRunning;
        var paused = _app.IsPaused;

        var labels = new[]
        {
            "重启服务",
            paused ? "恢复同步" : "暂停同步",
            "连接详情"
        };

        // 未登录或服务未运行时禁用前两项；"连接详情" 始终可用
        var disabled = new HashSet<int>();
        if (!(loggedIn && running))
        {
            disabled.Add(0);
        }
        if (!running)
        {
            disabled.Add(1);
        }

        if (!TryGetPopupAnchor(_hBtnServiceMenu, out var left, out var top, out var width))
        {
            return;
        }

        var picked = FluentPopupMenu.ShowWithSeparators(
            _hwnd,
            left,
            top,
            width,
            labels,
            selectedIndex: -1,
            _isDarkMode,
            disabled,
            new HashSet<int> { 2 });

        switch (picked)
        {
            case 0:
                _ = RestartServiceAsync();
                break;
            case 1:
                _app.TogglePause();
                break;
            case 2:
                ShowConnectionDetailsDialog();
                break;
        }
    }

    private void ShowConnectionDetailsDialog()
    {
        var wsUrl = string.IsNullOrWhiteSpace(_currentWebsocketUrl) ? "无" : _currentWebsocketUrl;
        NativeClipboard.TrySetText(wsUrl);

        var detailsText = $"WebSocket 地址：\n{wsUrl}\n\n状态：{(_app.ServiceRunning ? (_app.IsPaused ? "连接正常，同步已暂停" : "连接正常，同步已就绪") : "未连接")}\n\n（地址已自动复制到系统剪贴板）";
        FluentDialog.ShowInfo(_hwnd, "连接详情", detailsText, _isDarkMode);
    }

    private async Task SaveAndReconnectAsync()
    {
        SetBusy(true);

        try
        {
            SaveFormSettings();

            var typedPassword = GetControlText(_hPasswordBox);
            var data = _app.SettingsStore.Data;
            var hasPassword = !string.IsNullOrWhiteSpace(typedPassword)
                || (data.SavePassword && !string.IsNullOrWhiteSpace(data.SavedPassword));

            if (!hasPassword && !_app.IsLoggedIn)
            {
                SetProblem(UiText.RequiredLoginFields);
                return;
            }

            // 加密开关刚被打开但还没有派生密钥：只有重新登录才能派生出来。
            // 不在这里重登的话，已登录分支会"只保存不重连"，密钥一直是空的，
            // 之后重启服务就会以明文发送。
            var cipherNeedsKey = data.CipherEnabled && string.IsNullOrEmpty(data.DerivedKeyBase64);
            if (cipherNeedsKey && !hasPassword)
            {
                ReportFailure(UiText.EncryptionKeyMissing);
                return;
            }

            // 已登录且未输入新密码：只保存设置，不重新登录（密钥缺失时除外）
            var needsLogin = cipherNeedsKey || !(_app.IsLoggedIn && !hasPassword);
            if (needsLogin)
            {
                await _app.StopServiceAsync().ConfigureAwait(true);
                var rounds = int.TryParse(GetControlText(_hHashRoundsBox), out var r) && r > 0 ? r : ClipConfig.DefaultHashRounds;
                // 只有正在编辑且确实输入了新密码时才用输入值，否则沿用已保存的密码
                var loginPassword = _isEditingPassword && !string.IsNullOrWhiteSpace(typedPassword)
                    ? typedPassword
                    : data.SavedPassword;
                var request = new LoginRequest(
                    GetControlText(_hServerUrlBox),
                    GetControlText(_hUsernameBox),
                    loginPassword,
                    rounds,
                    GetControlText(_hSaltBox),
                    data.TrustAllCertificates,
                    data.ServerCertificateThumbprint);

                await _app.LoginAsync(request, _disposeCts.Token).ConfigureAwait(true);
            }

            LoadFromSettings();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            ReportFailure(UiText.FormatException(error));
        }
        finally
        {
            SetBusy(false);
            RefreshFromState();
        }
    }

    // 操作失败的用户可见反馈：提示卡片 + 明确弹窗（toast 一闪而过，失败原因需要用户读完）
    private void ReportFailure(string text)
    {
        SetProblem(text);
        FluentDialog.ShowInfo(_hwnd, UiText.OperationFailedTitle, text, _isDarkMode);
    }

    private async Task LogoutAsync()
    {
        SetBusy(true);
        try
        {
            await _app.LogoutAsync(_disposeCts.Token).ConfigureAwait(true);
            LoadFromSettings();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            ReportFailure(UiText.FormatException(error));
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
            ReportFailure(UiText.FormatException(error));
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
        NativeMethods.EnableWindow(_hBtnSaveAndReconnect, !busy);
        NativeMethods.EnableWindow(_hBtnAccount, !busy);
        NativeMethods.EnableWindow(_hBtnClose, !busy);
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

        // 窗口销毁前先收起悬停/提示卡片，否则会留下一个孤立的顶层窗口
        NativeMethods.KillTimer(_hwnd, PillPollTimerId);
        FluentTooltip.Hide();

        if (_fontTitle != IntPtr.Zero) NativeMethods.DeleteObject(_fontTitle);
        if (_fontCardTitle != IntPtr.Zero) NativeMethods.DeleteObject(_fontCardTitle);
        if (_fontBody != IntPtr.Zero) NativeMethods.DeleteObject(_fontBody);
        if (_fontCaption != IntPtr.Zero) NativeMethods.DeleteObject(_fontCaption);
        if (_fontCheck != IntPtr.Zero) NativeMethods.DeleteObject(_fontCheck);

        if (_hBrushBg != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushBg);
        if (_hBrushCard != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushCard);
        if (_hBrushEditBg != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushEditBg);
        if (_hBrushEditDisabledBg != IntPtr.Zero) NativeMethods.DeleteObject(_hBrushEditDisabledBg);
        if (_hPenBorder != IntPtr.Zero) NativeMethods.DeleteObject(_hPenBorder);
        if (_hPenEditBorder != IntPtr.Zero) NativeMethods.DeleteObject(_hPenEditBorder);
        if (_hPenEditDisabledBorder != IntPtr.Zero) NativeMethods.DeleteObject(_hPenEditDisabledBorder);

        if (_hwnd != IntPtr.Zero)
        {
            Instances.TryRemove(_hwnd, out _);
            NativeMethods.DestroyWindow(_hwnd);
        }
    }
}
