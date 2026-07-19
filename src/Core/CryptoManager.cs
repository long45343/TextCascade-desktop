using System.Security.Cryptography;
using System.Text;

namespace TextCascadeSharp.Core;

// 加密相关工具集合。提供：
//   - SHA3-512 hex 计算（用于密码本地校验）
//   - PBKDF2-SHA256 密钥派生（与 ClipCascade 各端互通）
//   - AES-GCM 加解密（基于自实现的 GcmCipher）
public static class CryptoManager
{
    // AES-256 密钥长度
    private const int AesKeyBytes = 32;

    // 计算字符串的 SHA3-512 hex（小写）。
    // 用于把明文密码变成 hash 后传给服务端做登录校验。
    // Python/JS/Android 端用相同算法，hash 值字面相同。
    public static string Sha3_512LowercaseHex(string input)
    {
        if (!SHA3_512.IsSupported)
        {
            // 不支持 SHA3 的运行时（旧 Windows / 旧 .NET）直接抛异常，
            // 不再走自实现的 Keccak fallback（review issue #2）
            throw new PlatformNotSupportedException(
                "SHA3-512 is not available on this platform. Run on Windows 10 build 16299+ or a .NET runtime with SHA3 support.");
        }
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA3_512.HashData(inputBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // 用 PBKDF2-SHA256 从密码派生 AES-256 密钥。
    // salt 构造为 username + rawPassword + saltSuffix，与 Python/JS/Android
    // 端完全相同 → 跨端派生出的密钥一致 → 可互相加解密剪贴板内容。
    // 这个 salt 构造不合规（review issue #3），但改动会破坏跨端互通，
    // 只能等 ClipCascade 上游协议升级时统一处理。
    public static byte[] DerivePasswordKey(string username, string rawPassword, string saltSuffix, int rounds)
    {
        var salt = Encoding.UTF8.GetBytes(username + rawPassword + saltSuffix);
        return Rfc2898DeriveBytes.Pbkdf2(
            rawPassword,
            salt,
            rounds,
            HashAlgorithmName.SHA256,
            AesKeyBytes);
    }

    // 用 AES-GCM 加密明文，返回 Base64 编码的 nonce/ciphertext/tag。
    // 本端默认生成 12 字节 nonce（与 JS/Android 端一致）。
    public static EncryptedPayload Encrypt(string plainText, string keyBase64)
    {
        var key = Convert.FromBase64String(keyBase64);
        // 使用自定义 GcmCipher 而非 .NET AesGcm：内置实现仅支持 12 字节 nonce，
        // 而 Python (pycryptodome) 端默认生成 16 字节 nonce。本端发送给
        // Python 端的消息 12 字节 nonce 可被 pycryptodome 正常解密；
        // Python 端发来的 16 字节 nonce 也必须由本端自定义实现解密。
        var (nonce, ciphertext, tag) = GcmCipher.Encrypt(key, Encoding.UTF8.GetBytes(plainText));
        return new EncryptedPayload(
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));
    }

    // 用 AES-GCM 解密 EncryptedPayload。
    // 支持任意 nonce 长度（12 或 16 字节均可）。
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
