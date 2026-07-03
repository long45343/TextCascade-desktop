namespace TextCascadeSharp.Core;

internal static class Sha3Fallback
{
    private const int Sha3_512RateBytes = 72;

    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001UL,
        0x0000000000008082UL,
        0x800000000000808aUL,
        0x8000000080008000UL,
        0x000000000000808bUL,
        0x0000000080000001UL,
        0x8000000080008081UL,
        0x8000000000008009UL,
        0x000000000000008aUL,
        0x0000000000000088UL,
        0x0000000080008009UL,
        0x000000008000000aUL,
        0x000000008000808bUL,
        0x800000000000008bUL,
        0x8000000000008089UL,
        0x8000000000008003UL,
        0x8000000000008002UL,
        0x8000000000000080UL,
        0x000000000000800aUL,
        0x800000008000000aUL,
        0x8000000080008081UL,
        0x8000000000008080UL,
        0x0000000080000001UL,
        0x8000000080008008UL
    ];

    private static readonly int[] RotationOffsets =
    [
        0, 1, 62, 28, 27,
        36, 44, 6, 55, 20,
        3, 10, 43, 25, 39,
        41, 45, 15, 21, 8,
        18, 2, 61, 56, 14
    ];

    public static byte[] Sha3_512(byte[] input)
    {
        var state = new ulong[25];
        var offset = 0;
        while (offset + Sha3_512RateBytes <= input.Length)
        {
            AbsorbBlock(state, input, offset);
            KeccakF1600(state);
            offset += Sha3_512RateBytes;
        }

        var block = new byte[Sha3_512RateBytes];
        var remaining = input.Length - offset;
        Buffer.BlockCopy(input, offset, block, 0, remaining);
        block[remaining] = 0x06;
        block[Sha3_512RateBytes - 1] |= 0x80;
        AbsorbBlock(state, block, 0);
        KeccakF1600(state);

        var output = new byte[64];
        var outputOffset = 0;
        var lane = 0;
        while (outputOffset < output.Length)
        {
            var value = state[lane];
            for (var index = 0; index < 8 && outputOffset < output.Length; index++)
            {
                output[outputOffset++] = (byte)((value >> (8 * index)) & 0xffUL);
            }
            lane++;
            if (lane * 8 == Sha3_512RateBytes && outputOffset < output.Length)
            {
                KeccakF1600(state);
                lane = 0;
            }
        }
        return output;
    }

    private static void AbsorbBlock(ulong[] state, byte[] block, int offset)
    {
        for (var lane = 0; lane < Sha3_512RateBytes / 8; lane++)
        {
            ulong value = 0;
            for (var index = 0; index < 8; index++)
            {
                value |= (ulong)block[offset + lane * 8 + index] << (8 * index);
            }
            state[lane] ^= value;
        }
    }

    private static void KeccakF1600(ulong[] state)
    {
        var c = new ulong[5];
        var d = new ulong[5];
        var b = new ulong[25];

        for (var round = 0; round < 24; round++)
        {
            for (var x = 0; x < 5; x++)
            {
                c[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];
            }
            for (var x = 0; x < 5; x++)
            {
                d[x] = c[(x + 4) % 5] ^ RotateLeft(c[(x + 1) % 5], 1);
            }
            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 5; y++)
                {
                    state[x + 5 * y] ^= d[x];
                }
            }

            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 5; y++)
                {
                    var source = x + 5 * y;
                    var targetX = y;
                    var targetY = (2 * x + 3 * y) % 5;
                    b[targetX + 5 * targetY] = RotateLeft(state[source], RotationOffsets[source]);
                }
            }

            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 5; y++)
                {
                    state[x + 5 * y] = b[x + 5 * y] ^ (~b[((x + 1) % 5) + 5 * y] & b[((x + 2) % 5) + 5 * y]);
                }
            }

            state[0] ^= RoundConstants[round];
        }
    }

    private static ulong RotateLeft(ulong value, int offset)
    {
        return (value << offset) | (value >> (64 - offset));
    }
}
