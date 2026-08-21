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
    public const string DirectionInbound = "inbound";
    public const string DirectionOutbound = "outbound";
}