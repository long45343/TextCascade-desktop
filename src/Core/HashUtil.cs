using System.Text;

namespace TextCascadeSharp.Core;

public static class HashUtil
{
    public static ulong Fnv1A64(string input)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        var encoder = Encoding.UTF8.GetEncoder();
        var chars = input.AsSpan();
        Span<byte> bytes = stackalloc byte[512];
        var completed = false;
        while (!completed)
        {
            encoder.Convert(chars, bytes, flush: true, out var charsUsed, out var bytesUsed, out completed);
            for (var index = 0; index < bytesUsed; index++)
            {
                hash ^= bytes[index];
                hash *= prime;
            }
            chars = chars[charsUsed..];
        }
        return hash;
    }
}
