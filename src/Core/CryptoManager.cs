using System.Security.Cryptography;
using System.Text;

namespace TextCascadeSharp.Core;

// 加密相关工具集合。提供：
//   - PBKDF2-HMAC-SHA256 密钥派生（与 Windows/Android 双端约定一致）
//   - AES-GCM 加解密（基于内置 System.Security.Cryptography.AesGcm，
//     nonce 固定 12 字节（96-bit）、tag 固定 16 字节、无 AAD）
public static class CryptoManager
{
    // AES-256 密钥长度
    private const int AesKeyBytes = 32;

    // 线格式常量（协议 v2.3.0 起）：nonce 12 字节、tag 16 字节
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    // 用 PBKDF2-HMAC-SHA256 从密码派生 AES-256 密钥。
    // 双端约定：salt = UTF-8(username + "$" + password + "$" + saltSuffix)，
    // 迭代 hashRounds（默认 664937），输出 32 字节。
    public static byte[] DerivePasswordKey(string username, string rawPassword, string saltSuffix, int rounds)
    {
        var salt = Encoding.UTF8.GetBytes(username + "$" + rawPassword + "$" + saltSuffix);
        return Rfc2898DeriveBytes.Pbkdf2(
            rawPassword,
            salt,
            rounds,
            HashAlgorithmName.SHA256,
            AesKeyBytes);
    }

    // 用 AES-GCM 加密明文，返回 Base64 编码的 nonce/ciphertext/tag。
    // 协议约定：nonce 固定 12 字节随机，tag 固定 16 字节。
    public static EncryptedPayload Encrypt(string plainText, string keyBase64)
    {
        var key = Convert.FromBase64String(keyBase64);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);
        return new EncryptedPayload(
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));
    }

    // 用 AES-GCM 解密 EncryptedPayload。仅接受 12 字节 nonce，
    // 其他长度直接拒绝（v2.3.0 起不再兼容 16 字节）。
    public static string Decrypt(EncryptedPayload payload, string keyBase64)
    {
        var key = Convert.FromBase64String(keyBase64);
        var nonce = Convert.FromBase64String(payload.Nonce);
        if (nonce.Length != NonceBytes)
        {
            throw new CryptographicException($"Unsupported GCM nonce length: {nonce.Length} bytes (expected {NonceBytes}).");
        }
        var ciphertext = Convert.FromBase64String(payload.Ciphertext);
        var tag = Convert.FromBase64String(payload.Tag);
        var plainBytes = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, ciphertext, tag, plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
