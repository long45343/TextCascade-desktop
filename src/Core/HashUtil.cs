using System.Text;

namespace TextCascadeSharp.Core;

public static class HashUtil
{
    public static ulong Fnv1A64(string input)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        foreach (var value in Encoding.UTF8.GetBytes(input))
        {
            hash ^= value;
            hash *= prime;
        }
        return hash;
    }
}
