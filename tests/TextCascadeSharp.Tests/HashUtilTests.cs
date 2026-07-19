using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

/// <summary>
/// FNV-1a 64-bit 已知向量测试。
/// 测试向量来源：http://www.isthe.com/chongo/tech/comp/fnv/
/// 注意：FNV 仅用于本地剪贴板去重，非安全用途。
/// </summary>
public class HashUtilTests
{
    [Fact]
    public void Fnv1A64_EmptyString_ReturnsOffsetBasis()
    {
        // FNV-1a 64-bit offset basis
        const ulong expected = 0xcbf29ce484222325UL;
        Assert.Equal(expected, HashUtil.Fnv1A64(string.Empty));
    }

    [Fact]
    public void Fnv1A64_SingleCharA_MatchesKnownVector()
    {
        // 来自 FNV 参考实现：fnv1a("a") = 0xaf63dc4c8601ec8c
        const ulong expected = 0xaf63dc4c8601ec8cUL;
        Assert.Equal(expected, HashUtil.Fnv1A64("a"));
    }

    [Fact]
    public void Fnv1A64_Foobar_MatchesKnownVector()
    {
        // 来自 FNV 参考实现：fnv1a("foobar") = 0x85944171f73967e8
        const ulong expected = 0x85944171f73967e8UL;
        Assert.Equal(expected, HashUtil.Fnv1A64("foobar"));
    }

    [Fact]
    public void Fnv1A64_DifferentStrings_ProduceDifferentHashes()
    {
        var h1 = HashUtil.Fnv1A64("hello");
        var h2 = HashUtil.Fnv1A64("world");
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void Fnv1A64_SameString_ProducesSameHash()
    {
        Assert.Equal(HashUtil.Fnv1A64("test"), HashUtil.Fnv1A64("test"));
    }

    [Fact]
    public void Fnv1A64_UnicodeContent_Deterministic()
    {
        // UTF-8 字节序列一致则 hash 一致
        var h1 = HashUtil.Fnv1A64("中文剪贴板");
        var h2 = HashUtil.Fnv1A64("中文剪贴板");
        Assert.Equal(h1, h2);
        Assert.NotEqual(0UL, h1);
    }
}
