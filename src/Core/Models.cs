using System.Text.Json.Serialization;

namespace TextCascadeSharp.Core;

// 持久化到 %APPDATA%/TextCascade/settings.json 的配置数据。
// 字段命名（snake_case）保持与 ClipCascade 其他客户端的设置文件兼容。
public sealed class SettingsData
{
    // ClipCascade 服务器 HTTP/HTTPS 入口（登录、注销、CSRF 获取）
    [JsonPropertyName("server_url")]
    public string ServerUrl { get; set; } = "http://localhost:8080";

    // STOMP-over-WebSocket 入口，登录成功后由 server_url 推导而来
    [JsonPropertyName("websocket_url")]
    public string WebsocketUrl { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    // SHA3-512(plaintext password) 的 hex，用于在本地校验用户输入的密码
    [JsonPropertyName("password_sha3")]
    public string PasswordSha3 { get; set; } = string.Empty;

    // PBKDF2 派生出的 AES-256 密钥（Base64），用于加解密剪贴板内容
    [JsonPropertyName("hashed_password_base64")]
    public string HashedPasswordBase64 { get; set; } = string.Empty;

    // Spring Security CSRF token，登录时从 /login 页面提取
    [JsonPropertyName("csrf_token")]
    public string CsrfToken { get; set; } = string.Empty;

    // 登录成功后的 JSESSIONID cookie，后续 STOMP 握手需要
    [JsonPropertyName("cookie_header")]
    public string CookieHeader { get; set; } = string.Empty;

    // 入站/出站剪贴板内容的最大字节数，超过则丢弃。512KB 与 Python 端默认一致
    [JsonPropertyName("max_size_bytes")]
    public long MaxSizeBytes { get; set; } = ClipConfig.DefaultMaxSizeBytes;

    // PBKDF2 迭代次数，与 Python 端默认 664937 一致以互通
    [JsonPropertyName("hash_rounds")]
    public int HashRounds { get; set; } = ClipConfig.DefaultHashRounds;

    // PBKDF2 salt 后缀，与 Python 端约定
    [JsonPropertyName("salt")]
    public string Salt { get; set; } = string.Empty;

    // 是否对剪贴板内容做 AES-GCM 加密
    [JsonPropertyName("cipher_enabled")]
    public bool CipherEnabled { get; set; } = true;

    // 是否在开机时自启动
    [JsonPropertyName("relaunch_on_boot")]
    public bool RelaunchOnBoot { get; set; }

    // WebSocket 连接状态变化时是否弹通知
    [JsonPropertyName("websocket_status_notification")]
    public bool WebsocketStatusNotification { get; set; }

    // 本地剪贴板读取时的最大字节数（防止把超大文件读到内存）
    [JsonPropertyName("local_max_clipboard_bytes")]
    public long LocalMaxClipboardBytes { get; set; } = ClipConfig.DefaultMaxSizeBytes;

    // 是否在本地保存密码（默认 false，仅保存 hash 用于校验）
    [JsonPropertyName("save_password")]
    public bool SavePassword { get; set; }

    // 保存的密码 hash，用于在重启后校验用户输入
    [JsonPropertyName("saved_password_hash")]
    public string SavedPasswordHash { get; set; } = string.Empty;
}

// 运行期使用的不可变配置快照，由 SettingsData 构造。
// 不可变 record 避免 TextSyncEngine 在并发场景下读到半更新的字段。
public sealed record ClipConfig(
    string ServerUrl,
    string WebsocketUrl,
    string Username,
    string PasswordSha3,
    string HashedPasswordBase64,
    string CsrfToken,
    string CookieHeader,
    long MaxSizeBytes,
    int HashRounds,
    string Salt,
    bool CipherEnabled,
    bool RelaunchOnBoot,
    bool WebsocketStatusNotification,
    long LocalMaxClipboardBytes)
{
    // PBKDF2 默认迭代次数。值与 Python/JS/Android 端完全相同，
    // 任意一端改动都会破坏跨端密钥一致性。
    public const int DefaultHashRounds = 664_937;

    // 默认内容大小上限（512KB），与 ClipCascade 各端约定一致
    public const long DefaultMaxSizeBytes = 512_000L;

    // 从持久化设置构造运行期配置快照
    public static ClipConfig FromSettings(SettingsStore store)
    {
        var data = store.Data;
        return new ClipConfig(
            data.ServerUrl,
            data.WebsocketUrl,
            data.Username,
            data.PasswordSha3,
            data.HashedPasswordBase64,
            data.CsrfToken,
            data.CookieHeader,
            data.MaxSizeBytes,
            data.HashRounds,
            data.Salt,
            data.CipherEnabled,
            data.RelaunchOnBoot,
            data.WebsocketStatusNotification,
            data.LocalMaxClipboardBytes);
    }

    // 把 http(s)://host:port/ 转成 ws(s)://host:port/clipsocket
    // 服务端 Spring Boot 注册的 WebSocket 端点固定为 /clipsocket
    public static string WebsocketUrlFromServerUrl(string serverUrl)
    {
        var trimmed = serverUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(UiText.InvalidServerUrl(trimmed));
        }
        var scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" :
            uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "ws" :
            throw new InvalidOperationException(UiText.UnsupportedServerUrlScheme(uri.Scheme));
        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Path = uri.AbsolutePath.TrimEnd('/') + "/clipsocket"
        };
        return builder.Uri.ToString();
    }
}

// UI 层向 App 层发起登录请求时携带的参数
public sealed record LoginRequest(
    string ServerUrl,
    string Username,
    string Password,
    int HashRounds,
    string Salt);

// 登录成功后 App 层返回给 UI 的结果（用于更新 settings.json）
public sealed record LoginResult(
    string NormalizedServerUrl,
    string WebsocketUrl,
    string PasswordSha3,
    string HashedPasswordBase64,
    string CsrfToken,
    string CookieHeader,
    long MaxSizeBytes);

// STOMP MESSAGE 帧的 JSON 主体。type 当前固定为 "text"
public sealed record ClipMessage(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("type")] string Type);

// AES-GCM 加密后的载荷。各字段均为 Base64 编码的字节序列。
// nonce 长度由发送方决定（本端 12 字节，Python 端 16 字节）。
public sealed record EncryptedPayload(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("ciphertext")] string Ciphertext,
    [property: JsonPropertyName("tag")] string Tag);
