using System.Text.Json.Serialization;

namespace TextCascadeSharp.Core;

// 持久化到 %APPDATA%/TextCascade/settings.json 的配置数据（snake_case、原子写）。
// 敏感字段（saved_password/auth_token/derived_key_b64）落盘前经 DPAPI 保护。
public sealed class SettingsData
{
    // 服务器 HTTPS 入口（登录）。占位默认值，实际部署地址由用户自行配置
    [JsonPropertyName("server_url")]
    public string ServerUrl { get; set; } = "https://your-server:8443";

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    // 登录返回的 Bearer token（DPAPI 保护）
    [JsonPropertyName("auth_token")]
    public string AuthToken { get; set; } = string.Empty;

    // token 过期时刻，RFC3339 UTC 字符串（Z 结尾）；空表示未知
    [JsonPropertyName("token_expires_at_utc")]
    public string TokenExpiresAtUtc { get; set; } = string.Empty;

    // 服务端协议版本
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; set; }

    // 服务端允许的单条文本最大字节数
    [JsonPropertyName("max_text_bytes")]
    public long MaxTextBytes { get; set; } = ClipConfig.DefaultMaxTextBytes;

    // 服务端 hello 超时（秒）
    [JsonPropertyName("hello_timeout_seconds")]
    public int HelloTimeoutSeconds { get; set; } = ClipConfig.DefaultHelloTimeoutSeconds;

    // 服务端心跳间隔（秒）
    [JsonPropertyName("heartbeat_interval_seconds")]
    public int HeartbeatIntervalSeconds { get; set; } = ClipConfig.DefaultHeartbeatIntervalSeconds;

    // 服务端心跳超时（秒）
    [JsonPropertyName("heartbeat_timeout_seconds")]
    public int HeartbeatTimeoutSeconds { get; set; } = ClipConfig.DefaultHeartbeatTimeoutSeconds;

    // 客户端唯一 ID（UUID v4），首次运行生成并持久化
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    // 客户端展示名（默认 Environment.MachineName）
    [JsonPropertyName("client_name")]
    public string ClientName { get; set; } = string.Empty;

    // 最近已知服务端版本号（无符号整数，0 表示未知/从未收到）
    [JsonPropertyName("last_server_version")]
    public ulong LastServerVersion { get; set; }

    // PBKDF2 迭代次数
    [JsonPropertyName("hash_rounds")]
    public int HashRounds { get; set; } = ClipConfig.DefaultHashRounds;

    // PBKDF2 salt 后缀
    [JsonPropertyName("salt")]
    public string Salt { get; set; } = string.Empty;

    // PBKDF2 派生的 AES-256 密钥（Base64，DPAPI 保护）。
    // 持久化以支持“不保存原始密码仍可解密”
    [JsonPropertyName("derived_key_b64")]
    public string DerivedKeyBase64 { get; set; } = string.Empty;

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
    public long LocalMaxClipboardBytes { get; set; } = ClipConfig.DefaultMaxTextBytes;

    // 自签部署时是否信任所有证书
    [JsonPropertyName("trust_all_certificates")]
    public bool TrustAllCertificates { get; set; }

    // 自签部署时的服务端证书 SHA-256 指纹（空表示不限定，若开启 TrustAllCertificates 则无条件信任）
    [JsonPropertyName("server_certificate_thumbprint")]
    public string ServerCertificateThumbprint { get; set; } = string.Empty;

    // 是否在本地保存密码
    [JsonPropertyName("save_password")]
    public bool SavePassword { get; set; }

    // 保存的密码（DPAPI 保护），重启后用于派生密钥和自动登录
    [JsonPropertyName("saved_password")]
    public string SavedPassword { get; set; } = string.Empty;

    // 序列化前使用浅拷贝，避免把内存中的明文敏感字段改成密文
    internal SettingsData ShallowCopy() => (SettingsData)MemberwiseClone();
}

// 运行期使用的不可变配置快照，由 SettingsData 构造。
public sealed record ClipConfig(
    string ServerUrl,
    string AuthToken,
    DateTime? TokenExpiresAtUtc,
    string Username,
    string ClientId,
    string ClientName,
    ulong LastServerVersion,
    long MaxTextBytes,
    int HelloTimeoutSeconds,
    int HeartbeatIntervalSeconds,
    int HeartbeatTimeoutSeconds,
    int HashRounds,
    string Salt,
    string DerivedKeyBase64,
    bool CipherEnabled,
    bool TrustAllCertificates,
    string ServerCertificateThumbprint,
    bool RelaunchOnBoot,
    bool WebsocketStatusNotification,
    long LocalMaxClipboardBytes)
{
    // 本客户端支持的 textcascade 协议版本。服务端 protocolVersion 高于该值时
    // 拒绝建立 WebSocket 并提示升级
    public const int SupportedProtocolVersion = 1;

    // WebSocket 子协议名
    public const string SubProtocol = "textcascade.v1";

    // PBKDF2 默认迭代次数（与 Android 端约定一致）
    public const int DefaultHashRounds = 664_937;

    // 默认内容大小上限
    public const long DefaultMaxTextBytes = 512_000L;

    // 登录响应缺失对应字段时的兜底值（与服务端默认配置 §3.1 一致：5/30/60）
    public const int DefaultHelloTimeoutSeconds = 5;
    public const int DefaultHeartbeatIntervalSeconds = 30;
    public const int DefaultHeartbeatTimeoutSeconds = 60;

    // 从持久化设置构造运行期配置快照
    public static ClipConfig FromSettings(SettingsStore store) => FromSettings(store.Data);

    public static ClipConfig FromSettings(SettingsData data)
    {
        return new ClipConfig(
            data.ServerUrl,
            data.AuthToken,
            ParseTokenExpiry(data.TokenExpiresAtUtc),
            data.Username,
            data.ClientId,
            data.ClientName,
            data.LastServerVersion,
            data.MaxTextBytes,
            data.HelloTimeoutSeconds,
            data.HeartbeatIntervalSeconds,
            data.HeartbeatTimeoutSeconds,
            data.HashRounds,
            data.Salt,
            data.DerivedKeyBase64,
            data.CipherEnabled,
            data.TrustAllCertificates,
            data.ServerCertificateThumbprint,
            data.RelaunchOnBoot,
            data.WebsocketStatusNotification,
            data.LocalMaxClipboardBytes);
    }

    // WebSocket 入口由 server_url 派生：https://host:port → wss://host:port/api/v1/sync
    public string WebsocketUrl => WebsocketUrlFromServerUrl(ServerUrl);

    public static string WebsocketUrlFromServerUrl(string serverUrl)
    {
        var trimmed = serverUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new CoreException(ErrorCodes.InvalidServerUrl, trimmed);
        }
        var scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" :
            uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "ws" :
            throw new CoreException(ErrorCodes.UnsupportedServerUrlScheme, uri.Scheme);
        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Path = "/api/v1/sync"
        };
        return builder.Uri.ToString();
    }

    private static DateTime? ParseTokenExpiry(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return JsonUtil.ParseRfc3339Utc(value);
    }
}

// UI 层向 App 层发起登录请求时携带的参数（password 为原始密码，经 TLS 上送）
public sealed record LoginRequest(
    string ServerUrl,
    string Username,
    string Password,
    int HashRounds,
    string Salt,
    bool TrustAllCertificates,
    string ServerCertificateThumbprint = "");

// 登录成功后 App 层返回给 UI 的结果（用于更新 settings.json）
public sealed record LoginResult(
    string NormalizedServerUrl,
    string Token,
    DateTime ExpiresAtUtc,
    int ProtocolVersion,
    long MaxTextBytes,
    int HelloTimeoutSeconds,
    int HeartbeatIntervalSeconds,
    int HeartbeatTimeoutSeconds);

// hello 附带的本地快照：当前本地剪贴板文本（非空时）
public sealed record HelloSnapshot(
    string Payload,
    bool Encrypted,
    string Hash,
    string LocalModifiedAtUtc);

// 客户端 → 服务端：hello
public sealed record HelloMessage(
    string ClientId,
    string ClientName,
    ulong LastServerVersion,
    HelloSnapshot? Snapshot);

// welcome.latest：服务端当前最新值（无内容时为 null）
public sealed record WelcomeLatest(
    ulong Version,
    string Payload,
    bool Encrypted,
    string Hash,
    string? FromClientId,
    string? UpdatedAtUtc);

// 服务端 → 客户端：welcome
public sealed record WelcomeMessage(WelcomeLatest? Latest);

// 客户端 → 服务端：clip（version 由服务端分配，上行不含）
public sealed record OutboundClipMessage(
    string Id,
    string Payload,
    bool Encrypted,
    string Hash);

// 服务端 → 客户端：clip 广播
public sealed record InboundClipMessage(
    ulong Version,
    string Payload,
    bool Encrypted,
    string Hash,
    string? Id = null,
    string? FromClientId = null,
    string? FromClientName = null,
    string? UpdatedAtUtc = null);

// 服务端 → 客户端：clip_ack
public sealed record ClipAckMessage(
    string Id,
    ulong Version,
    string? UpdatedAtUtc = null);

// 服务端 → 客户端：ping
public sealed record PingMessage(string? ServerTimeUtc);

// 客户端 → 服务端：pong
public sealed record PongMessage(string ClientTimeUtc);

// 服务端 → 客户端：bye
public sealed record ByeMessage(string? Reason);

// 服务端 → 客户端：error（§5.6：referenceId 关联被拒的客户端 clip id）
public sealed record ErrorMessage(string Code, string? Message, string? ReferenceId = null);

// AES-GCM 加密后的载荷（payload 字段的紧凑 JSON 结构）。各字段均为 Base64。
// nonce 长度由发送方决定（本端默认 16 字节，解密时兼容 12/16 字节）。
public sealed record EncryptedPayload(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("ciphertext")] string Ciphertext,
    [property: JsonPropertyName("tag")] string Tag);
