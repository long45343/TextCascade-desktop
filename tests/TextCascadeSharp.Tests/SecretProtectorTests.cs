using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

/// <summary>
/// SecretProtector DPAPI 测试。仅 Windows 环境运行（本测试项目目标即为 net10.0-windows）。
/// </summary>
public class SecretProtectorTests
{
    [Fact]
    public void Protect_Unprotect_RoundTrip()
    {
        const string secret = "MyP@ssw0rd!中文";

        var protectedValue = SecretProtector.Protect(secret);
        var restored = SecretProtector.Unprotect(protectedValue);

        Assert.StartsWith("dpapi:", protectedValue, StringComparison.Ordinal);
        Assert.Equal(secret, restored);
    }

    [Fact]
    public void Protect_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SecretProtector.Protect(string.Empty));
        Assert.Equal(string.Empty, SecretProtector.Protect(""));
    }

    [Fact]
    public void Unprotect_LegacyPlaintext_PassesThrough()
    {
        Assert.Equal("legacy-plain", SecretProtector.Unprotect("legacy-plain"));
    }

    [Fact]
    public void TryUnprotect_TamperedCiphertext_ReturnsFalse()
    {
        var protectedValue = SecretProtector.Protect("secret");
        var chars = protectedValue.ToCharArray();
        chars[6] = chars[6] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);

        Assert.False(SecretProtector.TryUnprotect(tampered, out var value));
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void TryUnprotect_GarbageBase64_ReturnsFalse()
    {
        Assert.False(SecretProtector.TryUnprotect("dpapi:!!!not-base64!!!", out var value));
        Assert.Equal(string.Empty, value);
    }
}
