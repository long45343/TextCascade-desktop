using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TextCascadeSharp.Core;

// HTTPS 登录与 WebSocket 同步共用的证书校验策略。
// 此前两条通道各写一套：HTTP 在系统信任时短路（指纹失效），
// WebSocket 只在“信任所有”时才装回调（指纹模式完全不校验）。
// 统一到这里后，规则只有一份，两条通道不会再漂移。
internal static class TlsPolicy
{
    // 是否需要安装自定义校验回调。
    // 指纹模式与全信任模式需要，系统默认校验则保持不安装。
    public static bool RequiresCustomValidation(bool trustAllCertificates, string? thumbprint)
    {
        return trustAllCertificates || ThumbprintNormalizer.Normalize(thumbprint).Length > 0;
    }

    // 统一判定：填了指纹就只认指纹（系统信任与否都不影响）；
    // 没填指纹且勾选全信任才无条件放行；两者都没有则使用系统校验结果。
    public static bool IsCertificateAcceptable(
        X509Certificate? certificate,
        bool trustAllCertificates,
        string? thumbprint,
        SslPolicyErrors sslPolicyErrors)
    {
        var normalized = ThumbprintNormalizer.Normalize(thumbprint);
        if (normalized.Length > 0)
        {
            try
            {
                return ClipApiClient.ValidateCertificateThumbprint(certificate, normalized);
            }
            catch (CryptographicException)
            {
                // 远端证书句柄异常：视为不匹配
                return false;
            }
        }

        if (trustAllCertificates)
        {
            return true;
        }

        return sslPolicyErrors == SslPolicyErrors.None;
    }
}
