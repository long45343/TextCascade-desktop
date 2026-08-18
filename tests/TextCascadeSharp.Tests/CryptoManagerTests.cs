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

    // nonce 生成 16 字节随机（双端约定；解密时兼容 12/16 字节）
    [Fact]
    public void Encrypt_Generates16ByteNonce()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var payload = CryptoManager.Encrypt("secret", key);
        Assert.Equal(16, Convert.FromBase64String(payload.Nonce).Length);
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
        Assert.Throws<CryptographicException>(() => CryptoManager.Decrypt(tampered, key));
    }

    // 解密侧兼容 12 字节 nonce（旧端/其他端互通）
    [Fact]
    public void Decrypt_Accepts12ByteNonce()
    {
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var key = Convert.ToBase64String(keyBytes);
        var (nonce, ciphertext, tag) = GcmCipher.Encrypt(keyBytes, Encoding.UTF8.GetBytes("interop"), nonce: new byte[12]);
        var payload = new EncryptedPayload(
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));
        Assert.Equal("interop", CryptoManager.Decrypt(payload, key));
    }
}
