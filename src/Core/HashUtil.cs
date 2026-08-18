using System.Text;

namespace TextCascadeSharp.Core;

// 非加密用 hash 工具。仅用于双端剪贴板内容去重，不参与任何安全流程。
public static class HashUtil
{
    // FNV-1a 64-bit。
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

    // 服务端契约的 hash 字段格式：明文 UTF-8 字节的 FNV-1a 64 位，
    // 小写十六进制（16 个字符）。服务端对该字段 opaque（上限 4096 字节），
    // 仅用于双端剪贴板去重。
    public static string Fnv1A64Hex(string input)
    {
        return Fnv1A64(input).ToString("x16");
    }
}
