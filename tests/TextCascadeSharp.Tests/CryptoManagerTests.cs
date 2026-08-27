using System.Security.Cryptography;
using System.Text;
using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

public class CryptoManagerTests
{
    // PBKDF2 salt 构造：username + "$" + password + "$" + salt（双端约定）
    [Fact]
    public void DerivePasswordKey_UsesDollarSeparatedSalt()
    {
        var derived = CryptoManager.DerivePasswordKey("alice", "pw", "salt", 1000);
        var expected = Rfc2898DeriveBytes.Pbkdf2(
            "pw",
            Encoding.UTF8.GetBytes("alice$pw$salt"),
            1000,
            HashAlgorithmName.SHA256,
            32);
        Assert.Equal(expected, derived);
    }

    [Fact]
    public void DerivePasswordKey_Deterministic()
    {
        var a = CryptoManager.DerivePasswordKey("alice", "pw", "salt", 1000);
        var b = CryptoManager.DerivePasswordKey("alice", "pw", "salt", 1000);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DerivePasswordKey_DifferentUserOrRoundsDiffer()
    {
        var base12 = CryptoManager.DerivePasswordKey("alice", "pw", "salt", 1000);
        Assert.NotEqual(base12, CryptoManager.DerivePasswordKey("bob", "pw", "salt", 1000));
        Assert.NotEqual(base12, CryptoManager.DerivePasswordKey("alice", "pw", "salt", 2000));
        // 与旧 salt 构造（无 $ 分隔）必须不同
        Assert.NotEqual(base12, Rfc2898DeriveBytes.Pbkdf2("pw", Encoding.UTF8.GetBytes("alicepwsalt"), 1000, HashAlgorithmName.SHA256, 32));
    }

    [Fact]
    public void DerivePasswordKey_OutputIs32Bytes()
    {
        Assert.Equal(32, CryptoManager.DerivePasswordKey("u", "p", "s", 10).Length);
    }

    // nonce 固定 12 字节随机（协议 v2.3.0 约定；解密仅接受 12 字节）
    [Fact]
    public void Encrypt_Generates12ByteNonce()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var payload = CryptoManager.Encrypt("secret", key);
        Assert.Equal(12, Convert.FromBase64String(payload.Nonce).Length);
        Assert.Equal(16, Convert.FromBase64String(payload.Tag).Length);
    }

    [Fact]
    public void Encrypt_NonceIsRandom()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var a = CryptoManager.Encrypt("secret", key);
        var b = CryptoManager.Encrypt("secret", key);
        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.NotEqual(a.Ciphertext, b.Ciphertext);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        const string text = "剪贴板内容 clipboard content";
        var payload = CryptoManager.Encrypt(text, key);
        Assert.Equal(text, CryptoManager.Decrypt(payload, key));
    }

    [Fact]
    public void EncryptDecrypt_EmptyPlainText()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var payload = CryptoManager.Encrypt("", key);
        Assert.Equal("", CryptoManager.Decrypt(payload, key));
    }

    [Fact]
    public void Decrypt_RejectsTamperedTag()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var payload = CryptoManager.Encrypt("secret", key);
        var tag = Convert.FromBase64String(payload.Tag);
        tag[0] ^= 1;
        var tampered = payload with { Tag = Convert.ToBase64String(tag) };
        // .NET 8+ 内置 AesGcm tag 不匹配抛派生类型 AuthenticationTagMismatchException
        Assert.Throws<AuthenticationTagMismatchException>(() => CryptoManager.Decrypt(tampered, key));
    }

    // 12 字节 nonce 经内置 AesGcm 加密后可解密（与实现无关的线格式回归）
    [Fact]
    public void Decrypt_Accepts12ByteNonce()
    {
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var key = Convert.ToBase64String(keyBytes);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var plaintext = Encoding.UTF8.GetBytes("interop");
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(keyBytes, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        var payload = new EncryptedPayload(
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));
        Assert.Equal("interop", CryptoManager.Decrypt(payload, key));
    }

    // NIST GCM Test Case 1（McGrew）：16 字节零密钥（AES-128）、零 12 字节 IV、空明文。
    // 解密方向 KAT：手构造线格式 payload，断言解密结果与期望 tag 一致。
    [Fact]
    public void Decrypt_NistTestCase1_EmptyPlaintext_12ByteNonce()
    {
        var key = Convert.ToBase64String(new byte[16]);
        var payload = new EncryptedPayload(
            Convert.ToBase64String(new byte[12]),
            Convert.ToBase64String(Array.Empty<byte>()),
            Convert.ToBase64String(Convert.FromHexString("58E2FCCEFA7E3061367F1D57A4E7455A")));
        Assert.Equal("", CryptoManager.Decrypt(payload, key));

        // 同输入下内置 AesGcm 产出相同 tag，防 KAT 常量手抄错误
        var tag = new byte[16];
        using (var aes = new AesGcm(new byte[16], 16))
        {
            aes.Encrypt(new byte[12], Array.Empty<byte>(), Array.Empty<byte>(), tag);
        }
        Assert.Equal("58E2FCCEFA7E3061367F1D57A4E7455A", Convert.ToHexString(tag));
    }

    // 非 12 字节 nonce 硬拒绝（v2.3.0 起不再兼容 16 字节）
    [Theory]
    [InlineData(16)]
    [InlineData(8)]
    public void Decrypt_RejectsNon12ByteNonce(int nonceLength)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var payload = new EncryptedPayload(
            Convert.ToBase64String(new byte[nonceLength]),
            Convert.ToBase64String(new byte[8]),
            Convert.ToBase64String(new byte[16]));
        var error = Assert.Throws<CryptographicException>(
            () => CryptoManager.Decrypt(payload, key));
        Assert.Contains("nonce", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // roundtrip 后线格式形状断言：nonce 恰 12 字节、tag 恰 16 字节
    [Fact]
    public void EncryptDecrypt_RoundTrip_12ByteNonce_PayloadShape()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        const string text = "形状校验 shape check";
        var payload = CryptoManager.Encrypt(text, key);
        Assert.Equal(text, CryptoManager.Decrypt(payload, key));
        Assert.Equal(12, Convert.FromBase64String(payload.Nonce).Length);
        Assert.Equal(16, Convert.FromBase64String(payload.Tag).Length);
    }
}
