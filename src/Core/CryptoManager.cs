using System.Security.Cryptography;
using System.Text;

namespace TextCascadeSharp.Core;

// 加密相关工具集合。提供：
//   - PBKDF2-HMAC-SHA256 密钥派生（与 Windows/Android 双端约定一致）
//   - AES-GCM 加解密（基于自实现的 GcmCipher，支持任意 nonce 长度）
public static class CryptoManager
{
    // AES-256 密钥长度
    private const int AesKeyBytes = 32;

    // 用 PBKDF2-HMAC-SHA256 从密码派生 AES-256 密钥。
    // 双端约定：salt = UTF-8(username + "$" + password + "$" + salt)，
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
    // 双端约定：nonce 默认生成 16 字节随机（解密侧兼容 12/16 字节）。
    public static EncryptedPayload Encrypt(string plainText, string keyBase64)
    {
        var key = Convert.FromBase64String(keyBase64);
        // 使用自定义 GcmCipher 而非 .NET AesGcm：内置实现仅支持 12 字节 nonce，
        // 双端互通要求 16 字节 nonce 也可发送/接收。
        var (nonce, ciphertext, tag) = GcmCipher.Encrypt(
            key,
            Encoding.UTF8.GetBytes(plainText),
            nonce: RandomNumberGenerator.GetBytes(16));
        return new EncryptedPayload(
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));
    }

    // 用 AES-GCM 解密 EncryptedPayload。支持任意 nonce 长度（12 或 16 字节均可）。
    public static string Decrypt(EncryptedPayload payload, string keyBase64)
    {
        var key = Convert.FromBase64String(keyBase64);
        var nonce = Convert.FromBase64String(payload.Nonce);
        var ciphertext = Convert.FromBase64String(payload.Ciphertext);
        var tag = Convert.FromBase64String(payload.Tag);
        var plainBytes = GcmCipher.Decrypt(key, nonce, ciphertext, tag);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
