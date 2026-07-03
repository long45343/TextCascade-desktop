using System.Text.Json.Serialization;

namespace TextCascadeSharp.Core;

public sealed class SettingsData
{
    [JsonPropertyName("server_url")]
    public string ServerUrl { get; set; } = "http://localhost:8080";

    [JsonPropertyName("websocket_url")]
    public string WebsocketUrl { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password_sha3")]
    public string PasswordSha3 { get; set; } = string.Empty;

    [JsonPropertyName("hashed_password_base64")]
    public string HashedPasswordBase64 { get; set; } = string.Empty;

    [JsonPropertyName("csrf_token")]
    public string CsrfToken { get; set; } = string.Empty;

    [JsonPropertyName("cookie_header")]
    public string CookieHeader { get; set; } = string.Empty;

    [JsonPropertyName("max_size_bytes")]
    public long MaxSizeBytes { get; set; } = ClipConfig.DefaultMaxSizeBytes;

    [JsonPropertyName("hash_rounds")]
    public int HashRounds { get; set; } = ClipConfig.DefaultHashRounds;

    [JsonPropertyName("salt")]
    public string Salt { get; set; } = string.Empty;

    [JsonPropertyName("cipher_enabled")]
    public bool CipherEnabled { get; set; } = true;

    [JsonPropertyName("relaunch_on_boot")]
    public bool RelaunchOnBoot { get; set; }

    [JsonPropertyName("websocket_status_notification")]
    public bool WebsocketStatusNotification { get; set; }

    [JsonPropertyName("local_max_clipboard_bytes")]
    public long LocalMaxClipboardBytes { get; set; } = ClipConfig.DefaultMaxSizeBytes;

    [JsonPropertyName("save_password")]
    public bool SavePassword { get; set; }

    [JsonPropertyName("saved_password_hash")]
    public string SavedPasswordHash { get; set; } = string.Empty;
}

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
    public const int DefaultHashRounds = 664_937;
    public const long DefaultMaxSizeBytes = 512_000L;

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

    public static string WebsocketUrlFromServerUrl(string serverUrl)
    {
        var trimmed = serverUrl.Trim().TrimEnd('/');
        var uri = new Uri(trimmed, UriKind.Absolute);
        var scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" :
            uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "ws" :
            throw new InvalidOperationException($"Unsupported server URL scheme: {uri.Scheme}");
        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Path = uri.AbsolutePath.TrimEnd('/') + "/clipsocket"
        };
        return builder.Uri.ToString();
    }
}

public sealed record LoginRequest(
    string ServerUrl,
    string Username,
    string Password,
    int HashRounds,
    string Salt);

public sealed record LoginResult(
    string NormalizedServerUrl,
    string WebsocketUrl,
    string PasswordSha3,
    string HashedPasswordBase64,
    string CsrfToken,
    string CookieHeader,
    long MaxSizeBytes);

public sealed record ClipMessage(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("type")] string Type);

public sealed record EncryptedPayload(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("ciphertext")] string Ciphertext,
    [property: JsonPropertyName("tag")] string Tag);
