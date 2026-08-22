using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;

namespace TextCascadeSharp.Core;

// AES-GCM 实现，参考 NIST SP 800-38D。
//
// 必须自行实现而非使用 .NET 内置 AesGcm：内置实现严格要求 96-bit (12 字节)
// nonce，但 ClipCascade 各客户端互通时存在不同 nonce 长度——
// Python (pycryptodome) 默认 16 字节，JS (react-native-aes-gcm-crypto) 默认
// 12 字节。本实现支持任意长度 nonce，与所有参考客户端互通。
internal static class GcmCipher
{
    private const int BlockBytes = 16;
    private static readonly byte[] BitReverseTable = CreateBitReverseTable();

    private static byte[] CreateBitReverseTable()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var b = (byte)i;
            table[i] = (byte)(((b * 0x80200802UL) & 0x0884422110UL) * 0x0101010101UL >> 32);
        }
        return table;
    }

    public static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(
        byte[] key, byte[] plaintext, byte[]? nonce = null)
    {
        // 默认生成 12 字节 nonce，与 JS/Android 端默认长度一致
        nonce ??= RandomNumberGenerator.GetBytes(12);
        using var cipher = new AesBlockCipher(key);
        var h = cipher.EncryptBlock(new byte[BlockBytes]);
        var j0 = ComputeJ0(nonce, h);
        var counter = IncrementCounter32(j0);
        var ciphertext = Gctr(counter, plaintext, cipher);
        var tag = ComputeAuthTag(h, j0, ciphertext, cipher);
        return (nonce, ciphertext, tag);
    }

    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        using var cipher = new AesBlockCipher(key);
        var h = cipher.EncryptBlock(new byte[BlockBytes]);
        var j0 = ComputeJ0(nonce, h);
        var counter = IncrementCounter32(j0);
        var plaintext = Gctr(counter, ciphertext, cipher);
        var expectedTag = ComputeAuthTag(h, j0, ciphertext, cipher);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, tag))
        {
            throw new CryptographicException("GCM authentication tag mismatch.");
        }
        return plaintext;
    }

    // J0 = IV || 0^31 || 1   (当 len(IV) = 96 bit)
    //   否则 J0 = GHASH(H, IV || 0^s || 0^64 || [len(IV)]_64)
    //   其中 s = 128*ceil(len(IV)/128) - len(IV) bit
    private static byte[] ComputeJ0(byte[] nonce, byte[] h)
    {
        if (nonce.Length == 12)
        {
            var j0 = new byte[BlockBytes];
            Buffer.BlockCopy(nonce, 0, j0, 0, 12);
            j0[15] = 1;
            return j0;
        }
        var ivBits = (ulong)nonce.Length * 8;
        var paddedBits = (ivBits + 127) & ~127UL;
        var totalBytes = (int)(paddedBits / 8) + 16;
        var data = new byte[totalBytes];
        Buffer.BlockCopy(nonce, 0, data, 0, nonce.Length);
        // 中间 8 字节为 0，最后 8 字节是 ivBits 的 big-endian
        for (var i = 0; i < 8; i++)
        {
            data[totalBytes - 8 + i] = (byte)(ivBits >> (56 - 8 * i));
        }
        return Ghash(h, data);
    }

    private static byte[] Ghash(byte[] h, byte[] data)
    {
        Span<byte> y = stackalloc byte[BlockBytes];
        y.Clear();

        for (var offset = 0; offset < data.Length; offset += BlockBytes)
        {
            for (var i = 0; i < BlockBytes && offset + i < data.Length; i++)
            {
                y[i] ^= data[offset + i];
            }
            Gf128Multiply(y, h, y);
        }
        return y.ToArray();
    }

    // GF(2^128) 乘法：硬件指令（Pclmulqdq + Sse2）优先，不支持时降级为软件逐位模拟。
    internal static void Gf128Multiply(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y, Span<byte> destination)
    {
        if (x.Length != BlockBytes || y.Length != BlockBytes || destination.Length != BlockBytes)
        {
            throw new ArgumentOutOfRangeException(null, $"All spans must be exactly {BlockBytes} bytes.");
        }

        if (Pclmulqdq.IsSupported && Sse2.IsSupported)
        {
            Gf128MultiplyPclmulqdq(x, y, destination);
        }
        else
        {
            Gf128MultiplySoftware(x, y, destination);
        }
    }

    // 基于 x86 PCLMULQDQ 硬件无进位乘法与标准多项式模归约 (x^128 + x^7 + x^2 + x + 1)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Gf128MultiplyPclmulqdq(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y, Span<byte> destination)
    {
        Span<byte> xNorm = stackalloc byte[16];
        Span<byte> yNorm = stackalloc byte[16];
        for (var i = 0; i < 16; i++)
        {
            xNorm[i] = BitReverseTable[x[i]];
            yNorm[i] = BitReverseTable[y[i]];
        }

        var xLow = BinaryPrimitives.ReadUInt64LittleEndian(xNorm[..8]);
        var xHigh = BinaryPrimitives.ReadUInt64LittleEndian(xNorm[8..]);
        var yLow = BinaryPrimitives.ReadUInt64LittleEndian(yNorm[..8]);
        var yHigh = BinaryPrimitives.ReadUInt64LittleEndian(yNorm[8..]);

        var a = Vector128.Create(xLow, xHigh);
        var b = Vector128.Create(yLow, yHigh);

        // 4 次 64x64 无进位乘法得到 256 位积
        var t0 = Pclmulqdq.CarrylessMultiply(a, b, 0x00).AsUInt64(); // aLow * bLow
        var t1 = Pclmulqdq.CarrylessMultiply(a, b, 0x10).AsUInt64(); // aHigh * bLow
        var t2 = Pclmulqdq.CarrylessMultiply(a, b, 0x01).AsUInt64(); // aLow * bHigh
        var t3 = Pclmulqdq.CarrylessMultiply(a, b, 0x11).AsUInt64(); // aHigh * bHigh

        var mid = Sse2.Xor(t1.AsByte(), t2.AsByte()).AsUInt64();

        var c0 = t0.GetElement(0);
        var c1 = t0.GetElement(1) ^ mid.GetElement(0);
        var c2 = t3.GetElement(0) ^ mid.GetElement(1);
        var c3 = t3.GetElement(1);

        // 归约：模 P(x) = x^128 + x^7 + x^2 + x + 1, 多项式为 0x87
        var poly = Vector128.Create(0x87UL, 0UL);
        var cHighVec = Vector128.Create(c2, c3);

        var r0 = Pclmulqdq.CarrylessMultiply(cHighVec, poly, 0x00).AsUInt64();
        var r1 = Pclmulqdq.CarrylessMultiply(cHighVec, poly, 0x01).AsUInt64();

        var d0 = c0 ^ r0.GetElement(0);
        var d1 = c1 ^ r0.GetElement(1) ^ r1.GetElement(0);
        var overflow = r1.GetElement(1);

        var overflowVec = Vector128.Create(overflow, 0UL);
        var r2Vec = Pclmulqdq.CarrylessMultiply(overflowVec, poly, 0x00).AsUInt64();

        d0 ^= r2Vec.GetElement(0);
        d1 ^= r2Vec.GetElement(1);

        Span<byte> resNorm = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(resNorm[..8], d0);
        BinaryPrimitives.WriteUInt64LittleEndian(resNorm[8..], d1);

        for (var i = 0; i < 16; i++)
        {
            destination[i] = BitReverseTable[resNorm[i]];
        }
    }

    // GF(2^128) 软件逐位模拟算法（NIST SP 800-38D Algorithm 1）
    internal static void Gf128MultiplySoftware(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y, Span<byte> destination)
    {
        Span<byte> xCopy = stackalloc byte[BlockBytes];
        x.CopyTo(xCopy);
        Span<byte> v = stackalloc byte[BlockBytes];
        y.CopyTo(v);

        destination.Clear();

        for (var i = 0; i < 128; i++)
        {
            // X 的第 i 位（bit 0 在 X[0] 的最高位）
            if ((xCopy[i / 8] & (0x80 >> (i % 8))) != 0)
            {
                for (var j = 0; j < BlockBytes; j++)
                {
                    destination[j] ^= v[j];
                }
            }
            // V 右移 1 位（big-endian）
            var lsb = (v[15] & 1) != 0;
            for (var j = 15; j > 0; j--)
            {
                v[j] = (byte)((v[j] >> 1) | ((v[j - 1] & 1) << 7));
            }
            v[0] >>= 1;
            if (lsb)
            {
                v[0] ^= 0xE1; // R = 0xE1 || 0^120
            }
        }
    }

    // GCTR 块加解密：复用 16 字节 byte[] 缓冲区（选项 B），消除 N 次循环内的临时数组分配
    private static byte[] Gctr(byte[] icb, byte[] data, AesBlockCipher cipher)
    {
        if (data.Length == 0)
        {
            return Array.Empty<byte>();
        }
        var output = new byte[data.Length];
        var counter = (byte[])icb.Clone();
        var keyStream = new byte[BlockBytes]; // 循环外单次分配
        var offset = 0;
        while (offset < data.Length)
        {
            cipher.EncryptBlock(counter, keyStream);
            var n = Math.Min(BlockBytes, data.Length - offset);
            for (var i = 0; i < n; i++)
            {
                output[offset + i] = (byte)(data[offset + i] ^ keyStream[i]);
            }
            offset += n;
            IncrementCounter32InPlace(counter);
        }
        return output;
    }

    private static byte[] IncrementCounter32(byte[] j0)
    {
        var counter = (byte[])j0.Clone();
        IncrementCounter32InPlace(counter);
        return counter;
    }

    // 仅递增最后 4 字节（big-endian），高 12 字节保持不变
    private static void IncrementCounter32InPlace(byte[] block)
    {
        for (var i = 15; i >= 12; i--)
        {
            if (++block[i] != 0)
            {
                break;
            }
        }
    }

    private static byte[] ComputeAuthTag(byte[] h, byte[] j0, byte[] ciphertext, AesBlockCipher cipher)
    {
        // S = GHASH(H, A || 0^v || C || 0^u || [len(A)]_64 || [len(C)]_64)
        // 本项目不使用 AAD，所以 A || 0^v 部分为空，v = 0
        var cBits = (ulong)ciphertext.Length * 8;
        var cPaddedBytes = (ciphertext.Length + BlockBytes - 1) & ~(BlockBytes - 1);
        var sLen = cPaddedBytes + 16;
        var s = new byte[sLen];
        Buffer.BlockCopy(ciphertext, 0, s, 0, ciphertext.Length);
        // 最后 16 字节：[len(A)]_64 (0) || [len(C)]_64 (big-endian)
        for (var i = 0; i < 8; i++)
        {
            s[sLen - 8 + i] = (byte)(cBits >> (56 - 8 * i));
        }
        var ghash = Ghash(h, s);
        // T = GCTR_K(J0, S) = S_1 XOR E(K, J0)
        // 因 GHASH 输出恰为 16 字节（一块），GCTR 只产生一块
        var ej0 = cipher.EncryptBlock(j0);
        var tag = new byte[BlockBytes];
        for (var i = 0; i < BlockBytes; i++)
        {
            tag[i] = (byte)(ghash[i] ^ ej0[i]);
        }
        return tag;
    }

    private sealed class AesBlockCipher : IDisposable
    {
        private readonly ICryptoTransform _encryptor;

        public AesBlockCipher(byte[] key)
        {
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            _encryptor = aes.CreateEncryptor();
        }

        public byte[] EncryptBlock(byte[] block)
        {
            var output = new byte[BlockBytes];
            _encryptor.TransformBlock(block, 0, BlockBytes, output, 0);
            return output;
        }

        public void EncryptBlock(byte[] block, byte[] destination)
        {
            _encryptor.TransformBlock(block, 0, BlockBytes, destination, 0);
        }

        public void Dispose() => _encryptor.Dispose();
    }
}
