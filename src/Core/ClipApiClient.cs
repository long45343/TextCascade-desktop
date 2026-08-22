using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TextCascadeSharp.Core;

// TextCascade 服务端 HTTP API 客户端。
// 协议：POST /api/v1/login，JSON {username, password}（原始密码经 TLS 上送），
// 成功返回 {token, expiresAtUtc, protocolVersion, maxTextBytes,
// helloTimeoutSeconds, heartbeatIntervalSeconds, heartbeatTimeoutSeconds}。
// 错误：401 invalid_credentials / 429 rate_limited / protocolVersion > 1。
public sealed class ClipApiClient
{
    // 登录流程：一次 POST 拿到 Bearer token 与全部服务端参数。
    // 本客户端只支持协议版本 1（ClipConfig.SupportedProtocolVersion）。
    public async Task<LoginResult> LoginAsync(
        string serverUrl,
        string username,
        string rawPassword,
        bool trustAllCertificates,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null,
        string serverCertificateThumbprint = "")
    {
        var normalizedServerUrl = SettingsStore.NormalizeServerUrl(serverUrl);
        var cleanThumbprint = NormalizeThumbprint(serverCertificateThumbprint);
        if (trustAllCertificates && string.IsNullOrEmpty(cleanThumbprint))
        {
            Logger.Log("[SECURITY] TrustAllCertificates is enabled without a certificate thumbprint; TLS MITM protection is inactive.");
        }

        HttpClientHandler? defaultHandler = null;
        if (handler is null)
        {
            defaultHandler = new HttpClientHandler();
            if (trustAllCertificates)
            {
                if (string.IsNullOrEmpty(cleanThumbprint))
                {
                    // 自签部署场景且未限定指纹：信任所有证书
                    defaultHandler.ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }
                else
                {
                    // 证书指纹固定（Certificate Pinning）：匹配指定的 SHA-256 指纹
                    defaultHandler.ServerCertificateCustomValidationCallback =
                        (_, cert, _, sslPolicyErrors) =>
                        {
                            if (sslPolicyErrors == SslPolicyErrors.None)
                            {
                                return true;
                            }
                            return ValidateCertificateThumbprint(cert, serverCertificateThumbprint);
                        };
                }
            }
        }
        // 登录是用户手动/恢复流程触发的低频操作，短生命周期 HttpClient 即可
        using var client = new HttpClient(handler ?? defaultHandler!, disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        using var content = new StringContent(JsonUtil.LoginRequest(username, rawPassword), Encoding.UTF8, "application/json");
        using var response = await client
            .PostAsync(normalizedServerUrl + "/api/v1/login", content, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // 401 invalid_credentials：界面提示"用户名或密码错误"，不启动引擎
            throw new InvalidCredentialException(ErrorCodes.InvalidCredentials);
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // 429 rate_limited：提示稍后再试，自动重登退避至少 30s（App 层负责）
            throw new RateLimitedException(ErrorCodes.LoginRateLimited);
        }
        if (!response.IsSuccessStatusCode)
        {
            // 500/502/503 等属于服务端临时故障，不应终止自动恢复
            throw new CoreException(ErrorCodes.LoginRequestFailed, $"HTTP {(int)response.StatusCode}");
        }

        var token = JsonUtil.StringField(body, "token");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new CoreException(ErrorCodes.LoginResponseInvalid);
        }
        var expiresAtUtc = JsonUtil.ParseRfc3339Utc(JsonUtil.StringField(body, "expiresAtUtc"))
            ?? throw new CoreException(ErrorCodes.LoginResponseInvalid);
        var protocolVersion = (int)JsonUtil.LongField(body, "protocolVersion", 0);
        if (protocolVersion != ClipConfig.SupportedProtocolVersion)
        {
            // 高于客户端支持的版本：不建立 WebSocket，明确提示升级（显示服务端版本号）
            throw new ProtocolVersionNotSupportedException(protocolVersion, ClipConfig.SupportedProtocolVersion);
        }

        return new LoginResult(
            normalizedServerUrl,
            token,
            expiresAtUtc,
            protocolVersion,
            JsonUtil.LongField(body, "maxTextBytes", ClipConfig.DefaultMaxTextBytes),
            (int)JsonUtil.LongField(body, "helloTimeoutSeconds", ClipConfig.DefaultHelloTimeoutSeconds),
            (int)JsonUtil.LongField(body, "heartbeatIntervalSeconds", ClipConfig.DefaultHeartbeatIntervalSeconds),
            (int)JsonUtil.LongField(body, "heartbeatTimeoutSeconds", ClipConfig.DefaultHeartbeatTimeoutSeconds));
    }

    public static bool ValidateCertificateThumbprint(X509Certificate? cert, string expectedThumbprint)
    {
        var normalizedExpected = NormalizeThumbprint(expectedThumbprint);
        if (string.IsNullOrEmpty(normalizedExpected) || cert is null)
        {
            return false;
        }

        string actualSha256;
        if (cert is X509Certificate2 cert2)
        {
            actualSha256 = cert2.GetCertHashString(HashAlgorithmName.SHA256);
        }
        else
        {
            using var temp = new X509Certificate2(cert);
            actualSha256 = temp.GetCertHashString(HashAlgorithmName.SHA256);
        }

        return string.Equals(actualSha256, normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return string.Empty;
        }
        return thumbprint.Replace(":", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Trim()
            .ToUpperInvariant();
    }
}
