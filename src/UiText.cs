using System.Globalization;
using System.Text.RegularExpressions;
using TextCascadeSharp.Core;

namespace TextCascadeSharp;

// UI 文案集中管理。所有面向用户的字符串都通过本类获取，
// 根据系统语言自动切换中英文。
// 使用方式：
//   - 静态字段：Label/按钮文字等固定文案
//   - 静态方法：带参数的动态文案（错误信息等）
internal static class UiText
{
    // 系统语言是否为中文。其他语言统一回退到英文。
    private static readonly bool UseChinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public static string AlreadyRunning => Text("TextCascade is already running.", "TextCascade 已在运行。");
    public static string ServerUrl => Text("Server URL", "服务器地址");
    public static string Username => Text("Username", "用户名");
    public static string Password => Text("Password", "密码");
    public static string Connection => Text("Connection", "连接");
    public static string HashRounds => Text("Hash Rounds", "哈希轮数");
    public static string EncryptionSalt => Text("Encryption Salt", "加密盐");
    public static string LocalMaxClipboardBytes => Text("Local Max Clipboard Bytes", "本地剪贴板上限（字节）");
    public static string EnableEncryption => Text("Enable Encryption", "启用加密");
    public static string SavePassword => Text("Save Password", "保存密码");
    public static string StartWithWindows => Text("Start with Windows", "开机启动");
    public static string WebSocketStatusNotification => Text("WebSocket Status Notification", "WebSocket 状态通知");
    public static string TrustAllCertificates => Text("Trust All Certificates", "信任所有证书");
    public static string TrustCertWarningTitle => Text("Security Warning", "安全警告");
    public static string TrustCertWarningBody => Text(
        "Trusting all certificates disables TLS certificate validation, so an attacker on the network could intercept your password and unencrypted clipboard data. Enable only for a trusted self-signed server network.",
        "信任所有证书会关闭 TLS 证书校验，网络中的攻击者可能截获你的密码与未加密剪贴板内容。仅应在可信的自签服务器内网中使用。");
    public static string SecurityAndLimits => Text("Security and Limits", "安全与限制");
    public static string Login => Text("Login", "登录");
    public static string Logout => Text("Logout", "注销");
    public static string RestartService => Text("Restart Service", "重启服务");
    public static string Start => Text("Start", "启动");
    public static string Stop => Text("Stop", "停止");
    public static string Service => Text("Service", "服务");
    public static string Status => Text("Status", "状态");
    public static string Session => Text("Session", "会话");
    public static string WebSocket => "WebSocket";
    public static string Idle => Text("Idle", "空闲");
    public static string LoggedIn => Text("Logged in", "已登录");
    public static string NotLoggedIn => Text("Not logged in", "未登录");
    public static string None => Text("None", "无");
    public static string Running => Text("Running", "运行中");
    public static string Stopped => Text("Stopped", "已停止");
    public static string SavedPasswordPlaceholder => Text("Saved; leave empty to reuse", "已保存；留空则复用");
    public static string LoggingIn => Text("Logging in", "正在登录");
    public static string AutoLogin => Text("Auto-login on startup", "开机自动登录");
    public static string AutoLoginFailed(string error) => Text("Auto-login failed: ", "自动登录失败：") + error;
    public static string SessionExpiredPleaseLogin => Text("Session expired; please log in again.", "会话已过期，请重新登录。");
    public static string SessionRecovering => Text("Recovering session...", "正在恢复会话...");
    public static string LoginSuccessful => Text("Login successful", "登录成功");
    public static string Saving => Text("Saving and reconnecting...", "正在保存并重连...");
    public static string SaveSuccessful => Text("Settings saved", "设置已保存");
    public static string Save => Text("Save", "保存");
    public static string LoggedOut => Text("Logged out", "已注销");
    public static string LoginFirst => Text("Login first", "请先登录");
    public static string RemoteTextApplied => Text("Remote text applied", "已应用远程文本");
    public static string Show => Text("Show", "显示主窗口");
    public static string StartService => Text("Start Service", "启动服务");
    public static string StopService => Text("Stop Service", "停止服务");
    public static string Exit => Text("Exit", "退出");
    public static string ClipboardSource => Text("clipboard", "剪贴板");
    public static string DirectionInbound => Text("inbound", "入站");
    public static string DirectionOutbound => Text("outbound", "出站");
    public static string Connected => Text("Connected", "已连接");
    public static string Connecting => Text("Connecting...", "正在连接...");
    public static string Broadcasting => Text("Broadcasting", "正在广播");
    public static string RequiredLoginFields => Text("Server URL, username and password are required.", "请填写服务器地址、用户名和密码。");
    public static string InvalidCredentials => Text("Invalid username or password.", "用户名或密码错误");
    public static string LoginRateLimited => Text("Logging in too frequently; please retry later.", "登录过于频繁，请稍后再试");
    public static string LoginResponseInvalid => Text(
        "Login response is missing required fields (token/expiresAtUtc/protocolVersion).",
        "登录响应缺少必需字段（token/expiresAtUtc/protocolVersion）。");
    public static string TextTooLargeIgnored => Text("Text too large; ignored.", "文本过大已忽略");
    public static string RateLimitedPaused => Text("Rate limited; pausing sends for ~1s.", "发送过于频繁，已暂停约 1 秒");

    public static string StartupRegistrationFailed(string error) => Text("Startup registration failed: ", "注册开机启动失败：") + error;
    public static string LoginFailed(string error) => Text("Login failed: ", "登录失败：") + error;
    public static string LoginRejectedStatus(int statusCode) => UseChinese
        ? $"服务器拒绝登录（HTTP {statusCode}）"
        : $"Server rejected login (HTTP {statusCode})";
    public static string LoginRequestFailedStatus(int statusCode) => UseChinese
        ? $"登录请求失败（HTTP {statusCode}）"
        : $"Login request failed (HTTP {statusCode})";
    public static string ProtocolVersionUnsupported(int serverVersion, int clientVersion) => UseChinese
        ? $"服务端协议版本 {serverVersion} 高于本客户端支持的 {clientVersion}，请升级客户端。"
        : $"Server protocol version {serverVersion} is higher than supported {clientVersion}; please update this client.";
    public static string SubprotocolRejected => Text(
        "Fatal: WebSocket subprotocol negotiation failed (HTTP 400); auto-reconnect suspended.",
        "致命错误：WebSocket 子协议协商失败（HTTP 400），已停止自动重连");
    public static string LogoutFailed(string error) => Text("Logout failed: ", "注销失败：") + error;
    public static string RestartServiceFailed(string error) => Text("Restart service failed: ", "重启服务失败：") + error;
    public static string SaveFailed(string error) => Text("Save failed: ", "保存失败：") + error;
    public static string ClipboardWriteFailed(string error) => Text("Clipboard write failed: ", "写入剪贴板失败：") + error;
    public static string InboundError(string error) => Text("Inbound error: ", "接收数据失败：") + error;
    public static string Disconnected(string reason) => Text("Disconnected: ", "连接已断开：") + reason;
    public static string WebSocketError(string error) => Text("WebSocket error: ", "WebSocket 错误：") + error;
    public static string IgnoredNotConnected(string source) => UseChinese ? $"已忽略（{source}）：未连接" : $"Ignored ({source}): not connected";
    public static string ClipboardTooLarge(string direction, int bytes) => UseChinese
        ? $"剪贴板内容过大（{direction}）：{bytes} 字节"
        : $"Clipboard too large ({direction}): {bytes} bytes";
    public static string SettingsLoadFailed(string error) => Text("Settings file could not be loaded; defaults were used: ", "设置文件加载失败，已使用默认值：") + error;
    public static string InvalidServerUrl(string value) => UseChinese
        ? $"服务器地址无效：{value}"
        : $"Invalid server URL: {value}";
    public static string UnsupportedServerUrlScheme(string scheme) => UseChinese
        ? $"不支持的服务器地址协议：{scheme}（仅支持 http/https）"
        : $"Unsupported server URL scheme: {scheme} (only http/https are supported)";

    // 二选一返回中英文文案
    private static string Text(string english, string chinese) => UseChinese ? chinese : english;

    // 把 Core 层传出的状态信封（CoreStatus.Pack 编码）映射为中英文案。
    // 非信封字符串原样返回，因此对 UI 层本地调用的文案（直接传 UiText.X）无影响。
    public static string FormatStatus(string raw)
    {
        if (!CoreStatus.TryUnpack(raw, out var code, out var args))
        {
            return raw;
        }
        switch (code)
        {
            case ErrorCodes.Connected: return Connected;
            case ErrorCodes.Connecting: return Connecting;
            case ErrorCodes.Broadcasting: return Broadcasting;
            case ErrorCodes.SessionExpiredPleaseLogin: return SessionExpiredPleaseLogin;
            case ErrorCodes.SessionRecovering: return SessionRecovering;
            case ErrorCodes.LoginSuccessful: return LoginSuccessful;
            case ErrorCodes.TextTooLargeIgnored: return TextTooLargeIgnored;
            case ErrorCodes.RateLimitedPaused: return RateLimitedPaused;
            case ErrorCodes.Disconnected: return Disconnected(Arg(args, 0));
            case ErrorCodes.WebSocketError: return WebSocketError(Arg(args, 0));
            case ErrorCodes.InboundError: return InboundError(Arg(args, 0));
            case ErrorCodes.ClipboardWriteFailed: return ClipboardWriteFailed(Arg(args, 0));
            case ErrorCodes.AutoLoginFailed: return AutoLoginFailed(Arg(args, 0));
            case ErrorCodes.IgnoredNotConnected: return IgnoredNotConnected(Arg(args, 0));
            case ErrorCodes.ClipboardTooLarge:
                return ClipboardTooLarge(LocalizedDirection(Arg(args, 0)), TryParseInt(Arg(args, 1)));
            case ErrorCodes.FatalProtocolError: return SubprotocolRejected;
            default: return raw;
        }
    }

    // 把 Core 层异常携带的领域错误码映射为中英文案。
    // errorCode 为 ErrorCodes 常量；detail 为异常携带的技术性细节（可选）。
    public static string FormatError(string errorCode, string? detail)
    {
        switch (errorCode)
        {
            case ErrorCodes.InvalidCredentials: return InvalidCredentials;
            case ErrorCodes.LoginRateLimited: return LoginRateLimited;
            case ErrorCodes.LoginRequestFailed: return LoginRequestFailedStatus(ExtractHttpStatus(detail));
            case ErrorCodes.LoginResponseInvalid: return LoginResponseInvalid;
            case ErrorCodes.InvalidServerUrl: return InvalidServerUrl(detail ?? errorCode);
            case ErrorCodes.UnsupportedServerUrlScheme: return UnsupportedServerUrlScheme(detail ?? errorCode);
            case ErrorCodes.SubprotocolRejected: return SubprotocolRejected;
            default: return detail ?? errorCode;
        }
    }

    // 取信封参数列表第 index 项；越界返回空串
    private static string Arg(string[] args, int index) => index >= 0 && index < args.Length ? args[index] : "";

    // 方向领域码 → 本地化方向文案；未知方向原样返回
    private static string LocalizedDirection(string direction)
    {
        if (direction == ErrorCodes.DirectionOutbound) return DirectionOutbound;
        if (direction == ErrorCodes.DirectionInbound) return DirectionInbound;
        return direction;
    }

    private static int TryParseInt(string value) => int.TryParse(value, out var parsed) ? parsed : 0;

    // 从 LoginRequestFailed 的 detail（形如 "HTTP 500"）提取状态码，取不到时回退 -1
    private static int ExtractHttpStatus(string? detail)
    {
        if (detail is not null)
        {
            var match = Regex.Match(detail, @"\d{3}");
            if (match.Success && int.TryParse(match.Value, out var status))
            {
                return status;
            }
        }
        return -1;
    }
}
