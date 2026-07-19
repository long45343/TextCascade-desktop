using System.Text;

namespace TextCascadeSharp.Core;

// 非加密用 hash 工具。仅用于本地剪贴板内容去重，不参与任何安全流程。
public static class HashUtil
{
    // FNV-1a 64-bit。
    // 选 FNV 而非 SHA/xxhash 的原因：实现简单、无外部依赖、对短文本（剪贴板
    // 通常 < 1KB）足够快且碰撞率可接受。
    // 算法：hash = (hash XOR byte) * prime，初值 offset_basis
    // 参考：https://datatracker.ietf.org/doc/html/draft-eastlake-fnv
    public static ulong Fnv1A64(string input)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        var bytes = Encoding.UTF8.GetBytes(input);
        for (var index = 0; index < bytes.Length; index++)
        {
            hash ^= bytes[index];
            hash *= prime;
        }
        return hash;
    }
}
