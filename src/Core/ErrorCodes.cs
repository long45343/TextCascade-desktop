namespace TextCascadeSharp.Core;

// Core 层领域错误码/状态码常量。Core 层所有面向 UI 的状态与异常都通过本类表达，
// 不再引用 UI 文案；中英文案本地化映射统一在 UI 层（FormatStatus / FormatError）完成。
public static class ErrorCodes
{
    // Login / 认证相关
    public const string InvalidCredentials = "invalid_credentials";
    public const string LoginRateLimited = "login_rate_limited";
    public const string LoginRequestFailed = "login_request_failed";
    public const string LoginResponseInvalid = "login_response_invalid";
    public const string InvalidServerUrl = "invalid_server_url";
    public const string UnsupportedServerUrlScheme = "unsupported_server_url_scheme";
    public const string SubprotocolRejected = "subprotocol_rejected";

    // Status 领域码
    public const string TextTooLargeIgnored = "text_too_large_ignored";
    public const string RateLimitedPaused = "rate_limited_paused";
    public const string RateLimitedDropped = "rate_limited_dropped";
    public const string Disconnected = "disconnected";
    public const string WebSocketError = "websocket_error";
    public const string SessionExpiredPleaseLogin = "session_expired_please_login";
    public const string Connecting = "connecting";
    public const string Connected = "connected";
    public const string ClipboardWriteFailed = "clipboard_write_failed";
    public const string InboundError = "inbound_error";
    public const string IgnoredNotConnected = "ignored_not_connected";
    public const string ClipboardTooLarge = "clipboard_too_large";
    public const string Broadcasting = "broadcasting";
    public const string SessionRecovering = "session_recovering";
    public const string LoginSuccessful = "login_successful";
    public const string AutoLoginFailed = "auto_login_failed";
    public const string FatalProtocolError = "fatal_protocol_error";
    // 加密已开启但派生密钥缺失：必须先重新登录派生密钥，否则只能明文发送
    public const string EncryptionKeyMissing = "encryption_key_missing";
    // 剪贴板监听无法注册（AddClipboardFormatListener 失败）：同步不会工作
    public const string ClipboardListenerUnavailable = "clipboard_listener_unavailable";
    public const string DirectionInbound = "inbound";
    public const string DirectionOutbound = "outbound";
}