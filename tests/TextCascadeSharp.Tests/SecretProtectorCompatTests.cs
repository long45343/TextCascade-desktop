using System.Security.Cryptography;
using System.Text;
using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

// 验证 SecretProtector 换用 BCL ProtectedData 后，密文格式与 v1 手写 P/Invoke 版本完全兼容。
// v1 的 CryptProtectData 调用参数为：entropy = UTF8("TextCascade.v1")、dwFlags = CRYPTPROTECT_UI_FORBIDDEN、
// CurrentUser 作用域、输出为原始 blob 后再 "dpapi:" + Base64。
// ProtectedData.Protect(..., CurrentUser) 走的是同一个 crypt32 调用，因此下面的手工等价路径
// 产出的密文必须能被 SecretProtector.Unprotect 解开，反之亦然。
public class SecretProtectorCompatTests
{
    private static readonly byte[] V1Entropy = Encoding.UTF8.GetBytes("TextCascade.v1");

    [Fact]
    public void LegacyV1Ciphertext_DecryptsWithProtectedDataImplementation()
    {
        const string plaintext = "P@ssw0rd-中文-span-v1";
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);

        // 按 v1 的参数手工产出一份密文（等价于 v1 的 ProtectCore 输出）
        var legacyBlob = ProtectedData.Protect(plainBytes, V1Entropy, DataProtectionScope.CurrentUser);
        var legacyStored = "dpapi:" + Convert.ToBase64String(legacyBlob);

        // 移植后的实现必须能解开
        Assert.Equal(plaintext, SecretProtector.Unprotect(legacyStored));
        Assert.True(SecretProtector.TryUnprotect(legacyStored, out var viaTry));
        Assert.Equal(plaintext, viaTry);
    }

    [Fact]
    public void NewCiphertext_DecryptsWithLegacyV1Parameters()
    {
        const string plaintext = "roundtrip-中文-2";
        var stored = SecretProtector.Protect(plaintext);
        Assert.StartsWith("dpapi:", stored, StringComparison.Ordinal);

        // 按 v1 的参数手工解回（等价于 v1 的 UnprotectCore）
        var blob = Convert.FromBase64String(stored["dpapi:".Length..]);
        var back = ProtectedData.Unprotect(blob, V1Entropy, DataProtectionScope.CurrentUser);
        Assert.Equal(plaintext, Encoding.UTF8.GetString(back));
    }

    [Fact]
    public void Protect_StillReturnsEmptyForEmptyInput()
    {
        Assert.Equal(string.Empty, SecretProtector.Protect(string.Empty));
    }

    [Fact]
    public void Unprotect_StillPassesThroughLegacyPlaintext()
    {
        Assert.Equal("legacy-plain", SecretProtector.Unprotect("legacy-plain"));
        Assert.True(SecretProtector.TryUnprotect("legacy-plain", out var value));
        Assert.Equal("legacy-plain", value);
    }

    [Fact]
    public void Unprotect_WrongEntropy_FailsAsCryptographicException()
    {
        var stored = SecretProtector.Protect("secret");
        var blob = Convert.FromBase64String(stored["dpapi:".Length..]);
        Assert.ThrowsAny<CryptographicException>(
            () => ProtectedData.Unprotect(blob, Encoding.UTF8.GetBytes("other-entropy"), DataProtectionScope.CurrentUser));
    }
}
