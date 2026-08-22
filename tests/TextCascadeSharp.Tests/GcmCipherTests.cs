using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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

    /// <summary>
    /// 验证 Pclmulqdq 硬件加速与软件逐位算法计算结果 100% 一致。
    /// </summary>
    [Fact]
    public void Gf128Multiply_Pclmulqdq_MatchesSoftware()
    {
        var expected = new byte[16];
        var actual = new byte[16];
        for (int i = 0; i < 100; i++)
        {
            var x = RandomNumberGenerator.GetBytes(16);
            var y = RandomNumberGenerator.GetBytes(16);
            GcmCipher.Gf128MultiplySoftware(x, y, expected);
            GcmCipher.Gf128MultiplyPclmulqdq(x, y, actual);
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
        }
    }

    [Theory]
    [InlineData(15, 16, 16)]
    [InlineData(16, 15, 16)]
    [InlineData(16, 16, 15)]
    [InlineData(0, 16, 16)]
    public void Gf128Multiply_InvalidLength_ThrowsArgumentOutOfRangeException(int xLen, int yLen, int destLen)
    {
        var x = new byte[xLen];
        var y = new byte[yLen];
        var dest = new byte[destLen];
        Assert.Throws<ArgumentOutOfRangeException>(() => GcmCipher.Gf128Multiply(x, y, dest));
    }

    [Fact]
    public void Gf128Multiply_AliasingDestination_ProducesCorrectResult()
    {
        for (int i = 0; i < 50; i++)
        {
            var x = RandomNumberGenerator.GetBytes(16);
            var y = RandomNumberGenerator.GetBytes(16);

            var expected = new byte[16];
            GcmCipher.Gf128Multiply(x, y, expected);

            // destination aliases x
            var inPlaceX = (byte[])x.Clone();
            GcmCipher.Gf128Multiply(inPlaceX, y, inPlaceX);
            Assert.Equal(expected, inPlaceX);

            // destination aliases y
            var inPlaceY = (byte[])y.Clone();
            GcmCipher.Gf128Multiply(x, inPlaceY, inPlaceY);
            Assert.Equal(expected, inPlaceY);
        }
    }

    [Fact]
    public void Ghash_ZeroHeapAllocation_InHotPath()
    {
        var h = RandomNumberGenerator.GetBytes(16);
        var x = RandomNumberGenerator.GetBytes(16);
        var dest = new byte[16];

        // JIT warm-up
        for (int i = 0; i < 10; i++)
        {
            GcmCipher.Gf128Multiply(x, h, dest);
        }

        var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            GcmCipher.Gf128Multiply(x, h, dest);
        }
        var afterAlloc = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, afterAlloc - beforeAlloc);
    }
    // 反转一个字节内的 8 个 bit
    private static byte ReverseBits(byte b)
    {
        return (byte)(((b * 0x80200802UL) & 0x0884422110UL) * 0x0101010101UL >> 32);
    }

    [Fact]
    public void TestStandardPolynomialReduction()
    {
        for (int iter = 0; iter < 100; iter++)
        {
            var x = RandomNumberGenerator.GetBytes(16);
            var y = RandomNumberGenerator.GetBytes(16);

            // 1. 转为标准多项式表示（bit i 对应 x^i）
            var xNorm = new byte[16];
            var yNorm = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                xNorm[i] = ReverseBits(x[i]);
                yNorm[i] = ReverseBits(y[i]);
            }

            // 加载到 Vector128 (xNorm[0..7] 作为 low, xNorm[8..15] 作为 high)
            var xLow = BitConverter.ToUInt64(xNorm, 0);
            var xHigh = BitConverter.ToUInt64(xNorm, 8);
            var yLow = BitConverter.ToUInt64(yNorm, 0);
            var yHigh = BitConverter.ToUInt64(yNorm, 8);

            var a = Vector128.Create(xLow, xHigh);
            var b = Vector128.Create(yLow, yHigh);

            // 256 位无进位乘法
            var t0 = Pclmulqdq.CarrylessMultiply(a, b, 0x00).AsUInt64(); // aLow * bLow
            var t1 = Pclmulqdq.CarrylessMultiply(a, b, 0x10).AsUInt64(); // aHigh * bLow
            var t2 = Pclmulqdq.CarrylessMultiply(a, b, 0x01).AsUInt64(); // aLow * bHigh
            var t3 = Pclmulqdq.CarrylessMultiply(a, b, 0x11).AsUInt64(); // aHigh * bHigh

            var mid = Sse2.Xor(t1.AsByte(), t2.AsByte()).AsUInt64();

            var c0 = t0.GetElement(0);
            var c1 = t0.GetElement(1) ^ mid.GetElement(0);
            var c2 = t3.GetElement(0) ^ mid.GetElement(1);
            var c3 = t3.GetElement(1);

            // C = (c3:c2)*x^128 + (c1:c0)
            // 归约：C_high * (x^7 + x^2 + x + 1)
            var poly = Vector128.Create(0x87UL, 0UL);
            var cHighVec = Vector128.Create(c2, c3);

            var r0 = Pclmulqdq.CarrylessMultiply(cHighVec, poly, 0x00).AsUInt64(); // c2 * 0x87
            var r1 = Pclmulqdq.CarrylessMultiply(cHighVec, poly, 0x01).AsUInt64(); // c3 * 0x87

            // r0:r1 是 128+7 位的多项式
            // r0 的低 64 位加到 c0，r0 的高 64 位加到 c1
            // r1 的低 64 位加到 c1，r1 的高 64 位（只有 7 位有效）需要再次归约！
            var d0 = c0 ^ r0.GetElement(0);
            var d1 = c1 ^ r0.GetElement(1) ^ r1.GetElement(0);
            var overflow = r1.GetElement(1); // 最高 7 位

            // 第二次折叠
            var r2 = overflow * 0x87UL; // 64-bit 乘法中 bit-xor 乘法即可，因为 7 位 * 8 位无进位
            // 用 Pclmulqdq 精确无进位乘法
            var overflowVec = Vector128.Create(overflow, 0UL);
            var r2Vec = Pclmulqdq.CarrylessMultiply(overflowVec, poly, 0x00).AsUInt64();

            d0 ^= r2Vec.GetElement(0);
            d1 ^= r2Vec.GetElement(1);

            // 转回 GCM 字节序
            var resNorm = new byte[16];
            BitConverter.TryWriteBytes(resNorm.AsSpan(0, 8), d0);
            BitConverter.TryWriteBytes(resNorm.AsSpan(8, 8), d1);

            var actual = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                actual[i] = ReverseBits(resNorm[i]);
            }

            var expected = new byte[16];
            GcmCipher.Gf128MultiplySoftware(x, y, expected);
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
        }
    }
}

