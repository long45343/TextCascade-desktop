using System.Globalization;

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
    public static string LocalMaxClipboardBytes => Text("Local Max Clipboard Bytes", "本地剪贴板上限(字节)");
    public static string EnableEncryption => Text("Enable Encryption", "启用加密");
    public static string SavePassword => Text("Save Password", "保存密码");
    public static string StartWithWindows => Text("Start with Windows", "开机启动");
    public static string WebSocketStatusNotification => Text("WebSocket Status Notification", "WebSocket 状态通知");
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
    public static string LoginSuccessful => Text("Login successful", "登录成功");
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
    public static string SavedPasswordEncryptionReuseError => Text(
        "Saved password cannot be reused for encryption; enter the password again.",
        "已保存的密码无法用于加密，请重新输入密码。");
    public static string FetchLoginPageFailed => Text("Failed to fetch login page", "获取登录页失败");
    public static string CsrfTokenNotFound => Text("No CSRF token found in login page.", "登录页中未找到 CSRF token。");
    public static string NoAuthenticatedSessionCookie => Text(
        "Login succeeded but no authenticated session cookie was retained.",
        "登录成功，但未保留认证会话 cookie。");

    public static string StartupRegistrationFailed(string error) => Text("Startup registration failed: ", "注册开机启动失败：") + error;
    public static string LoginFailed(string error) => Text("Login failed: ", "登录失败：") + error;
    public static string LoginRejectedStatus(int statusCode) => UseChinese
        ? $"服务器拒绝登录（HTTP {statusCode}）"
        : $"Server rejected login (HTTP {statusCode})";
    public static string LogoutFailed(string error) => Text("Logout failed: ", "注销失败：") + error;
    public static string RestartServiceFailed(string error) => Text("Restart service failed: ", "重启服务失败：") + error;
    public static string ClipboardWriteFailed(string error) => Text("Clipboard write failed: ", "写入剪贴板失败：") + error;
    public static string InboundError(string error) => Text("Inbound error: ", "接收数据失败：") + error;
    public static string Disconnected(string reason) => Text("Disconnected: ", "连接已断开：") + reason;
    public static string WebSocketError(string error) => Text("WebSocket error: ", "WebSocket 错误：") + error;
    public static string IgnoredNotConnected(string source) => UseChinese ? $"已忽略（{source}）：未连接" : $"Ignored ({source}): not connected";
    public static string ClipboardTooLarge(string direction, int bytes) => UseChinese
        ? $"剪贴板内容过大（{direction}）：{bytes} 字节"
        : $"Clipboard too large ({direction}): {bytes} bytes";
    public static string P2SOnly(string mode) => UseChinese
        ? $"此客户端仅支持 P2S；服务器返回 {mode}。"
        : $"This client supports P2S only; server returned {mode}.";
    public static string RequestFailedAfterLogin(string name) => UseChinese
        ? $"登录成功，但 {name} 请求失败"
        : $"Login succeeded but {name} request failed";
    public static string JsonExpectedAfterLogin(string name) => UseChinese
        ? $"登录成功，但 /{name} 返回 HTML 而不是 JSON；会话 cookie 未被接受。"
        : $"Login succeeded but /{name} returned HTML instead of JSON; session cookie was not accepted.";
    public static string RequestFailed(string prefix, int statusCode) => $"{prefix}: {statusCode}";
    public static string SettingsLoadFailed(string error) => Text("Settings file could not be loaded; defaults were used: ", "设置文件加载失败，已使用默认值：") + error;
    public static string InvalidServerUrl(string value) => UseChinese
        ? $"服务器地址无效：{value}"
        : $"Invalid server URL: {value}";
    public static string UnsupportedServerUrlScheme(string scheme) => UseChinese
        ? $"不支持的服务器地址协议：{scheme}（仅支持 http/https）"
        : $"Unsupported server URL scheme: {scheme} (only http/https are supported)";

    // 二选一返回中英文文案
    private static string Text(string english, string chinese) => UseChinese ? chinese : english;
}
