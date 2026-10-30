using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

// TLS 策略：HTTP 登录与 WebSocket 共用同一判定。
// 此前两条通道各写一套，导致"指纹模式 WebSocket 完全不校验"和
// "系统信任时 HTTP 跳过指纹比对"两个偏差。
public class TlsPolicyTests
{
    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("cn=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string ThumbprintOf(X509Certificate2 cert)
        => cert.GetCertHashString(HashAlgorithmName.SHA256);

    [Theory]
    [InlineData(true, "", true)]              // 全信任且无指纹 → 需要自定义校验（放行一切）
    [InlineData(true, "AABBCC", true)]        // 全信任 + 指纹 → 需要（指纹优先）
    [InlineData(false, "AABBCC", true)]       // 仅指纹 → 需要
    [InlineData(false, "", false)]            // 都没有 → 用系统默认，不装回调
    public void RequiresCustomValidation_MatchesMode(bool trustAll, string thumbprint, bool expected)
    {
        Assert.Equal(expected, TlsPolicy.RequiresCustomValidation(trustAll, thumbprint));
    }

    [Fact]
    public void ThumbprintMode_SelfSignedWithCorrectThumbprint_Accepts()
    {
        using var cert = CreateSelfSigned();
        var thumbprint = ThumbprintOf(cert);

        // 自签证书在系统看来是不可信（RemoteCertificateChainErrors），
        // 但指纹匹配时必须放行 —— 这正是此前 WebSocket 做不到的场景。
        var accepted = TlsPolicy.IsCertificateAcceptable(
            cert, trustAllCertificates: false, thumbprint,
            SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.True(accepted);
    }

    [Fact]
    public void ThumbprintMode_SelfSignedWithWrongThumbprint_Rejects()
    {
        using var cert = CreateSelfSigned();

        var accepted = TlsPolicy.IsCertificateAcceptable(
            cert, trustAllCertificates: false,
            "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF",
            SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.False(accepted);
    }

    [Fact]
    public void ThumbprintMode_SystemTrustedButMismatched_StillRejects()
    {
        using var cert = CreateSelfSigned();

        // 关键回归：证书被系统信任（sslPolicyErrors == None）也不能放过指纹不匹配的证书。
        // 旧实现先看 None 直接 return true，导致指纹固定形同虚设。
        var accepted = TlsPolicy.IsCertificateAcceptable(
            cert, trustAllCertificates: false,
            "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF",
            SslPolicyErrors.None);

        Assert.False(accepted);
    }

    [Fact]
    public void ThumbprintMode_SystemTrustedAndMatching_Accepts()
    {
        using var cert = CreateSelfSigned();
        var thumbprint = ThumbprintOf(cert);

        var accepted = TlsPolicy.IsCertificateAcceptable(
            cert, trustAllCertificates: false, thumbprint, SslPolicyErrors.None);

        Assert.True(accepted);
    }

    [Fact]
    public void TrustAllWithoutThumbprint_AcceptsAnyCertificate()
    {
        using var cert = CreateSelfSigned();

        var accepted = TlsPolicy.IsCertificateAcceptable(
            cert, trustAllCertificates: true, thumbprint: string.Empty,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.True(accepted);
    }

    [Fact]
    public void SystemDefaultMode_TrustedCertificate_Accepts()
    {
        using var cert = CreateSelfSigned();

        var accepted = TlsPolicy.IsCertificateAcceptable(
            cert, trustAllCertificates: false, thumbprint: string.Empty, SslPolicyErrors.None);

        Assert.True(accepted);
    }

    [Fact]
    public void SystemDefaultMode_UntrustedCertificate_Rejects()
    {
        using var cert = CreateSelfSigned();

        var accepted = TlsPolicy.IsCertificateAcceptable(
            cert, trustAllCertificates: false, thumbprint: string.Empty,
            SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.False(accepted);
    }

    [Fact]
    public void ThumbprintMode_ColonFormattedThumbprint_Matches()
    {
        using var cert = CreateSelfSigned();
        var raw = ThumbprintOf(cert);
        var formatted = string.Join(":", Enumerable.Range(0, raw.Length / 2)
            .Select(i => raw.Substring(i * 2, 2).ToLowerInvariant()));

        var accepted = TlsPolicy.IsCertificateAcceptable(
            cert, trustAllCertificates: false, formatted, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.True(accepted);
    }
}
