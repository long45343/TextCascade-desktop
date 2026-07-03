using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TextCascadeSharp.Core;

internal static class GcmCipher
{
    private const int BlockBytes = 16;
    private const int TagBytes = 16;

    public static (byte[] Ciphertext, byte[] Tag) Encrypt(byte[] key, byte[] nonce, byte[] plaintext)
    {
        using var aes = CreateAes(key);
        using var encryptor = aes.CreateEncryptor();
        var h = EncryptBlock(encryptor, new byte[BlockBytes]);
        var j0 = BuildInitialCounter(h, nonce);
        var ciphertext = Gctr(encryptor, IncrementCounter(j0), plaintext);
        var tag = Gctr(encryptor, j0, GHash(h, ciphertext));
        return (ciphertext, tag);
    }

    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        if (tag.Length != TagBytes)
        {
            throw new CryptographicException("Invalid AES-GCM tag length.");
        }

        using var aes = CreateAes(key);
        using var encryptor = aes.CreateEncryptor();
        var h = EncryptBlock(encryptor, new byte[BlockBytes]);
        var j0 = BuildInitialCounter(h, nonce);
        var expectedTag = Gctr(encryptor, j0, GHash(h, ciphertext));
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, tag))
        {
            throw new CryptographicException("AES-GCM authentication failed.");
        }

        return Gctr(encryptor, IncrementCounter(j0), ciphertext);
    }

    private static Aes CreateAes(byte[] key)
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        return aes;
    }

    private static byte[] BuildInitialCounter(byte[] h, byte[] nonce)
    {
        if (nonce.Length == 12)
        {
            var j0 = new byte[BlockBytes];
            Buffer.BlockCopy(nonce, 0, j0, 0, nonce.Length);
            j0[15] = 1;
            return j0;
        }

        var paddedLength = RoundUp(nonce.Length, BlockBytes) + BlockBytes;
        var data = new byte[paddedLength];
        Buffer.BlockCopy(nonce, 0, data, 0, nonce.Length);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(data.Length - 8), (ulong)nonce.Length * 8UL);
        return GHashRaw(h, data);
    }

    private static byte[] GHash(byte[] h, byte[] ciphertext)
    {
        var paddedLength = RoundUp(ciphertext.Length, BlockBytes) + BlockBytes;
        var data = new byte[paddedLength];
        Buffer.BlockCopy(ciphertext, 0, data, 0, ciphertext.Length);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(data.Length - 8), (ulong)ciphertext.Length * 8UL);
        return GHashRaw(h, data);
    }

    private static byte[] GHashRaw(byte[] h, byte[] data)
    {
        var y = new byte[BlockBytes];
        for (var offset = 0; offset < data.Length; offset += BlockBytes)
        {
            for (var index = 0; index < BlockBytes; index++)
            {
                y[index] ^= data[offset + index];
            }
            y = Multiply(y, h);
        }
        return y;
    }

    private static byte[] Gctr(ICryptoTransform encryptor, byte[] initialCounter, byte[] input)
    {
        if (input.Length == 0)
        {
            return Array.Empty<byte>();
        }

        var output = new byte[input.Length];
        var counter = (byte[])initialCounter.Clone();
        var streamBlock = new byte[BlockBytes];
        for (var offset = 0; offset < input.Length; offset += BlockBytes)
        {
            EncryptBlock(encryptor, counter, streamBlock);
            var blockLength = Math.Min(BlockBytes, input.Length - offset);
            for (var index = 0; index < blockLength; index++)
            {
                output[offset + index] = (byte)(input[offset + index] ^ streamBlock[index]);
            }
            IncrementCounterInPlace(counter);
        }
        return output;
    }

    private static byte[] EncryptBlock(ICryptoTransform encryptor, byte[] input)
    {
        var output = new byte[BlockBytes];
        EncryptBlock(encryptor, input, output);
        return output;
    }

    private static void EncryptBlock(ICryptoTransform encryptor, byte[] input, byte[] output)
    {
        var written = encryptor.TransformBlock(input, 0, BlockBytes, output, 0);
        if (written != BlockBytes)
        {
            throw new CryptographicException("AES block encryption failed.");
        }
    }

    private static byte[] IncrementCounter(byte[] counter)
    {
        var next = (byte[])counter.Clone();
        IncrementCounterInPlace(next);
        return next;
    }

    private static void IncrementCounterInPlace(byte[] counter)
    {
        for (var index = BlockBytes - 1; index >= BlockBytes - 4; index--)
        {
            counter[index]++;
            if (counter[index] != 0)
            {
                break;
            }
        }
    }

    private static byte[] Multiply(byte[] x, byte[] y)
    {
        var z = new byte[BlockBytes];
        var v = (byte[])y.Clone();
        for (var bit = 0; bit < 128; bit++)
        {
            if ((x[bit / 8] & (1 << (7 - bit % 8))) != 0)
            {
                XorInto(z, v);
            }

            var lsb = (v[15] & 1) != 0;
            ShiftRightOne(v);
            if (lsb)
            {
                v[0] ^= 0xe1;
            }
        }
        return z;
    }

    private static void XorInto(byte[] target, byte[] source)
    {
        for (var index = 0; index < BlockBytes; index++)
        {
            target[index] ^= source[index];
        }
    }

    private static void ShiftRightOne(byte[] value)
    {
        var carry = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var nextCarry = value[index] & 1;
            value[index] = (byte)((value[index] >> 1) | (carry << 7));
            carry = nextCarry;
        }
    }

    private static int RoundUp(int value, int factor)
    {
        return value == 0 ? 0 : ((value + factor - 1) / factor) * factor;
    }
}
