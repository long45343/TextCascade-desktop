using System.Security.Cryptography;
using System.Text;
using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

/// <summary>
/// GcmCipher 单元测试。
///
/// 测试策略：
///   1) NIST/McGrew Test Case 1（空明文 + 12 字节 IV）：标准 KAT 向量，验证基础路径
///   2) .NET 内置 AesGcm 作为 oracle：随机输入下我们的实现与 NIST 认证实现输出一致
///      （.NET AesGcm 是 NIST CAVP 认证的实现，作为参考权威可靠）
///   3) 16 字节 nonce 加密-解密往返：Python pycryptodome 互通场景的回归测试
///   4) 篡改检测：tag/ciphertext/nonce/key 任一被篡改都应导致解密失败
///
/// 测试向量来源：
///   - McGrew & Viega "The Galois/Counter Mode of Operation (GCM)" 附录
///     https://csrc.nist.rip/groups/ST/toolkit/BCM/documents/proposedmodes/gcm/gcm-spec.pdf
/// </summary>
public class GcmCipherTests
{
    private static byte[] Hex(string s) =>
        Convert.FromHexString(s.Replace(" ", string.Empty).ToUpperInvariant());

    /// <summary>
    /// NIST GCM Test Case 1: AES-128, 全零 key, 12 字节 IV, 空明文。
    /// 验证：空明文时 tag = E(K, J0)，J0 = IV || 0^31 || 1。
    /// 这是 NIST/McGrew 论文中最权威的测试向量，数据简单不易抄错。
    /// </summary>
    [Fact]
    public void NistTestCase1_EmptyPlaintext_12ByteNonce()
    {
        var key = Hex("00000000000000000000000000000000");
        var nonce = Hex("000000000000000000000000");
        var expectedTag = Hex("58E2FCCEFA7E3061367F1D57A4E7455A");

        var (usedNonce, ciphertext, tag) = GcmCipher.Encrypt(key, Array.Empty<byte>(), nonce);

        Assert.Equal(nonce, usedNonce);
        Assert.Empty(ciphertext);
        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// NIST GCM Test Case 1 反向验证：解密空明文应得到空明文，
    /// 且 tag 正确时不抛异常。
    /// </summary>
    [Fact]
    public void NistTestCase1_DecryptEmpty()
    {
        var key = Hex("00000000000000000000000000000000");
        var nonce = Hex("000000000000000000000000");
        var tag = Hex("58E2FCCEFA7E3061367F1D57A4E7455A");

        var plaintext = GcmCipher.Decrypt(key, nonce, Array.Empty<byte>(), tag);
        Assert.Empty(plaintext);
    }

    /// <summary>
    /// 与 .NET 内置 AesGcm 输出一致性验证（AES-128, 12 字节 nonce, 多块明文）。
    /// .NET AesGcm 是 NIST CAVP 认证实现，作为 oracle 权威可靠。
    /// </summary>
    [Fact]
    public void Encrypt_MatchesBuiltinAesGcm_Aes128_12ByteNonce()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = RandomNumberGenerator.GetBytes(60); // 跨多块

        var expectedCiphertext = new byte[60];
        var expectedTag = new byte[16];
        using var builtin = new AesGcm(key, tagSizeInBytes: 16);
        builtin.Encrypt(nonce, plaintext, expectedCiphertext, expectedTag, null);

        var (usedNonce, ciphertext, tag) = GcmCipher.Encrypt(key, plaintext, nonce);

        Assert.Equal(nonce, usedNonce);
        Assert.Equal(expectedCiphertext, ciphertext);
        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// 与 .NET 内置 AesGcm 输出一致性验证（AES-256, 12 字节 nonce, 单块明文）。
    /// </summary>
    [Fact]
    public void Encrypt_MatchesBuiltinAesGcm_Aes256_SingleBlock()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = RandomNumberGenerator.GetBytes(16);

        var expectedCiphertext = new byte[16];
        var expectedTag = new byte[16];
        using var builtin = new AesGcm(key, tagSizeInBytes: 16);
        builtin.Encrypt(nonce, plaintext, expectedCiphertext, expectedTag, null);

        var (usedNonce, ciphertext, tag) = GcmCipher.Encrypt(key, plaintext, nonce);

        Assert.Equal(nonce, usedNonce);
        Assert.Equal(expectedCiphertext, ciphertext);
        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// 与 .NET 内置 AesGcm 输出一致性验证（解密方向）。
    /// 用 .NET AesGcm 加密，用我们的 GcmCipher 解密，应得到原始明文。
    /// </summary>
    [Fact]
    public void Decrypt_MatchesBuiltinAesGcm_DecryptsBuiltinOutput()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes("decrypt builtin output");

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var builtin = new AesGcm(key, tagSizeInBytes: 16);
        builtin.Encrypt(nonce, plaintext, ciphertext, tag, null);

        var decrypted = GcmCipher.Decrypt(key, nonce, ciphertext, tag);
        Assert.Equal(plaintext, decrypted);
    }

    /// <summary>
    /// 16 字节 nonce 加密-解密往返测试。
    ///
    /// 这个测试场景对应 Python pycryptodome AES.new(key, AES.MODE_GCM) 的
    /// 默认行为（生成 16 字节 nonce）。这是修复"specified nonce is not a
    /// valid size"错误的回归测试。
    /// </summary>
    [Fact]
    public void RoundTrip_16ByteNonce_PythonInterop()
    {
        var key = RandomNumberGenerator.GetBytes(32); // AES-256
        var nonce16 = RandomNumberGenerator.GetBytes(16);
        var plaintext = "中文剪贴板内容 Hello ClipCascade 📋"u8.ToArray();

        var (usedNonce, ciphertext, tag) = GcmCipher.Encrypt(key, plaintext, nonce16);
        Assert.Equal(nonce16, usedNonce);

        var decrypted = GcmCipher.Decrypt(key, usedNonce, ciphertext, tag);
        Assert.Equal(plaintext, decrypted);
    }

    /// <summary>
    /// 12 字节 nonce 加密-解密往返测试（本端默认 nonce 长度）。
    /// </summary>
    [Fact]
    public void RoundTrip_12ByteNonce_DefaultBehavior()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = "TextCascade test payload"u8.ToArray();

        var (nonce, ciphertext, tag) = GcmCipher.Encrypt(key, plaintext);
        Assert.Equal(12, nonce.Length);

        var decrypted = GcmCipher.Decrypt(key, nonce, ciphertext, tag);
        Assert.Equal(plaintext, decrypted);
    }

    /// <summary>
    /// 8 字节 nonce 加密-解密往返测试（触发 GHASH 路径的 J0 计算）。
    /// 8 字节 nonce 既不是 12，也不是 16，专门测试 ComputeJ0 的非快路径。
    /// </summary>
    [Fact]
    public void RoundTrip_8ByteNonce_GhashPath()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce8 = RandomNumberGenerator.GetBytes(8);
        var plaintext = "Short nonce path test"u8.ToArray();

        var (usedNonce, ciphertext, tag) = GcmCipher.Encrypt(key, plaintext, nonce8);
        Assert.Equal(nonce8, usedNonce);

        var decrypted = GcmCipher.Decrypt(key, usedNonce, ciphertext, tag);
        Assert.Equal(plaintext, decrypted);
    }

    /// <summary>
    /// AES-192 密钥往返测试（虽然本客户端不使用，但 GcmCipher 应支持）。
    /// </summary>
    [Fact]
    public void RoundTrip_Aes192Key()
    {
        var key = RandomNumberGenerator.GetBytes(24); // AES-192
        var plaintext = "24-byte key round-trip"u8.ToArray();

        var (nonce, ciphertext, tag) = GcmCipher.Encrypt(key, plaintext);
        var decrypted = GcmCipher.Decrypt(key, nonce, ciphertext, tag);
        Assert.Equal(plaintext, decrypted);
    }

    /// <summary>
    /// 错误的 tag 必须导致解密失败（验证 AEAD 完整性）。
    /// 这是 AES-GCM 安全性的核心：任何密文或 tag 的篡改都必须被检测。
    /// </summary>
    [Fact]
    public void Decrypt_TamperedTag_ThrowsCryptographicException()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = "tamper detection test"u8.ToArray();
        var (nonce, ciphertext, tag) = GcmCipher.Encrypt(key, plaintext);

        // 篡改 tag 最后一字节
        tag[^1] ^= 0xFF;

        Assert.Throws<CryptographicException>(() =>
            GcmCipher.Decrypt(key, nonce, ciphertext, tag));
    }

    /// <summary>
    /// 篡改密文必须导致解密失败（验证 GHASH 不仅检查 tag 还与密文绑定）。
    /// </summary>
    [Fact]
    public void Decrypt_TamperedCiphertext_ThrowsCryptographicException()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = "tamper ciphertext test"u8.ToArray();
        var (nonce, ciphertext, tag) = GcmCipher.Encrypt(key, plaintext);

        // 篡改密文第一字节
        ciphertext[0] ^= 0xFF;

        Assert.Throws<CryptographicException>(() =>
            GcmCipher.Decrypt(key, nonce, ciphertext, tag));
    }

    /// <summary>
    /// 用错误密钥解密必须失败（验证密钥参与的 GHASH 与 GCTR）。
    /// </summary>
    [Fact]
    public void Decrypt_WrongKey_ThrowsCryptographicException()
    {
        var key1 = RandomNumberGenerator.GetBytes(32);
        var key2 = RandomNumberGenerator.GetBytes(32);
        var plaintext = "wrong key test"u8.ToArray();
        var (nonce, ciphertext, tag) = GcmCipher.Encrypt(key1, plaintext);

        Assert.Throws<CryptographicException>(() =>
            GcmCipher.Decrypt(key2, nonce, ciphertext, tag));
    }

    /// <summary>
    /// 篡改 nonce 必须导致解密失败。
    /// </summary>
    [Fact]
    public void Decrypt_TamperedNonce_ThrowsCryptographicException()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = "tamper nonce test"u8.ToArray();
        var (nonce, ciphertext, tag) = GcmCipher.Encrypt(key, plaintext);

        // 篡改 nonce 第一字节
        nonce[0] ^= 0xFF;

        Assert.Throws<CryptographicException>(() =>
            GcmCipher.Decrypt(key, nonce, ciphertext, tag));
    }

    /// <summary>
    /// 相同 key + nonce 加密两次必须得到相同结果（确定性验证）。
    /// 实际使用中 nonce 不会重复，但本测试确认实现是确定性的。
    /// </summary>
    [Fact]
    public void Encrypt_Deterministic_WithSameKeyAndNonce()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = "deterministic test"u8.ToArray();

        var r1 = GcmCipher.Encrypt(key, plaintext, nonce);
        var r2 = GcmCipher.Encrypt(key, plaintext, nonce);

        Assert.Equal(r1.Nonce, r2.Nonce);
        Assert.Equal(r1.Ciphertext, r2.Ciphertext);
        Assert.Equal(r1.Tag, r2.Tag);
    }

    /// <summary>
    /// 空明文也能正常加密-解密往返（边界用例）。
    /// </summary>
    [Fact]
    public void RoundTrip_EmptyPlaintext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);

        var (usedNonce, ciphertext, tag) = GcmCipher.Encrypt(key, Array.Empty<byte>(), nonce);
        Assert.Empty(ciphertext);

        var decrypted = GcmCipher.Decrypt(key, usedNonce, ciphertext, tag);
        Assert.Empty(decrypted);
    }
}
