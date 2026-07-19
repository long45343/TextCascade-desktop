using System.Security.Cryptography;
using System.Text;
using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

/// <summary>
/// CryptoManager 集成测试。
/// 覆盖：
///   - SHA3-512 输出格式
///   - PBKDF2 跨端密钥派生的确定性（与 ClipCascade Python 端 salt 构造一致）
///   - AES-GCM Encrypt/Decrypt 往返
/// </summary>
public class CryptoManagerTests
{
    [Fact]
    public void Sha3_512LowercaseHex_KnownVector()
    {
        // SHA3-512("") = a69f73cca23a9ac5c8b567dc185a756e97c982164fe25859e0d1dcc1475c80a615b2123af1f5f94c11e3e9402c3ac558f500199d95b6d3e301758586281dcd26
        var hex = CryptoManager.Sha3_512LowercaseHex("");
        Assert.Equal(128, hex.Length); // 64 字节 = 128 hex 字符
        Assert.All(hex, c => Assert.True(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'));
        Assert.Equal(
            "a69f73cca23a9ac5c8b567dc185a756e97c982164fe25859e0d1dcc1475c80a615b2123af1f5f94c11e3e9402c3ac558f500199d95b6d3e301758586281dcd26",
            hex);
    }

    [Fact]
    public void Sha3_512LowercaseHex_NonAsciiInput_ProducesValidHex()
    {
        var hex = CryptoManager.Sha3_512LowercaseHex("中文测试");
        Assert.Equal(128, hex.Length);
        Assert.All(hex, c => Assert.True(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'));
    }

    [Fact]
    public void DerivePasswordKey_Deterministic_SameInputsProduceSameKey()
    {
        var key1 = CryptoManager.DerivePasswordKey("alice", "password123", "salt-suffix", 1000);
        var key2 = CryptoManager.DerivePasswordKey("alice", "password123", "salt-suffix", 1000);

        Assert.Equal(32, key1.Length); // AES-256
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void DerivePasswordKey_DifferentUsers_ProduceDifferentKeys()
    {
        var k1 = CryptoManager.DerivePasswordKey("alice", "password", "salt", 100);
        var k2 = CryptoManager.DerivePasswordKey("bob", "password", "salt", 100);

        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void DerivePasswordKey_DifferentRounds_ProduceDifferentKeys()
    {
        var k1 = CryptoManager.DerivePasswordKey("u", "p", "s", 100);
        var k2 = CryptoManager.DerivePasswordKey("u", "p", "s", 200);

        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_PreservesPlaintext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var keyBase64 = Convert.ToBase64String(key);
        var plainText = "Hello ClipCascade 中文 📋";

        var payload = CryptoManager.Encrypt(plainText, keyBase64);

        // 验证 Base64 格式
        Assert.NotEmpty(payload.Nonce);
        Assert.NotEmpty(payload.Ciphertext);
        Assert.NotEmpty(payload.Tag);
        // nonce 应该是 12 字节（本端默认）
        Assert.Equal(12, Convert.FromBase64String(payload.Nonce).Length);

        var decrypted = CryptoManager.Decrypt(payload, keyBase64);
        Assert.Equal(plainText, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_EmptyPlaintext_RoundTrips()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var keyBase64 = Convert.ToBase64String(key);

        var payload = CryptoManager.Encrypt(string.Empty, keyBase64);
        // 明文为空时 ciphertext 也为空字节数组，Convert.ToBase64String 返回空字符串。
        // 这里只验证 nonce/tag 仍生成（解密必须依赖 tag 做完整性校验），
        // 并验证空明文可正确往返
        Assert.NotEmpty(payload.Nonce);
        Assert.NotEmpty(payload.Tag);
        Assert.Equal(string.Empty, payload.Ciphertext);

        var decrypted = CryptoManager.Decrypt(payload, keyBase64);
        Assert.Equal(string.Empty, decrypted);
    }

    [Fact]
    public void Encrypt_NonDeterministic_NonceRandom()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var keyBase64 = Convert.ToBase64String(key);
        var plainText = "same content";

        var p1 = CryptoManager.Encrypt(plainText, keyBase64);
        var p2 = CryptoManager.Encrypt(plainText, keyBase64);

        // nonce 不同 → ciphertext 不同 → tag 不同
        Assert.NotEqual(p1.Nonce, p2.Nonce);
        Assert.NotEqual(p1.Ciphertext, p2.Ciphertext);

        // 但都能解密回原文
        Assert.Equal(plainText, CryptoManager.Decrypt(p1, keyBase64));
        Assert.Equal(plainText, CryptoManager.Decrypt(p2, keyBase64));
    }

    [Fact]
    public void Decrypt_TamperedPayload_Throws()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var keyBase64 = Convert.ToBase64String(key);
        var payload = CryptoManager.Encrypt("secret", keyBase64);

        // 篡改 tag
        var tagBytes = Convert.FromBase64String(payload.Tag);
        tagBytes[0] ^= 0xFF;
        var tampered = payload with { Tag = Convert.ToBase64String(tagBytes) };

        Assert.ThrowsAny<CryptographicException>(() =>
            CryptoManager.Decrypt(tampered, keyBase64));
    }

    [Fact]
    public void Decrypt_With16ByteNoncePayload_Succeeds()
    {
        // 模拟 Python pycryptodome 端发来的 16 字节 nonce 消息
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(16); // Python 默认长度
        var plaintext = Encoding.UTF8.GetBytes("From Python client");
        var (nonceUsed, ciphertext, tag) = GcmCipher.Encrypt(key, plaintext, nonce);

        var payload = new EncryptedPayload(
            Convert.ToBase64String(nonceUsed),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));

        var decrypted = CryptoManager.Decrypt(payload, Convert.ToBase64String(key));
        Assert.Equal("From Python client", decrypted);
    }
}
