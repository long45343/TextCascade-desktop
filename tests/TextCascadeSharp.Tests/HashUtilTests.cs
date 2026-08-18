using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

public class HashUtilTests
{
    // FNV-1a 64 位已知向量
    [Theory]
    [InlineData("", "cbf29ce484222325")]
    [InlineData("a", "af63dc4c8601ec8c")]
    [InlineData("foobar", "85944171f73967e8")]
    public void Fnv1A64Hex_MatchesKnownVectors(string input, string expectedHex)
    {
        Assert.Equal(expectedHex, HashUtil.Fnv1A64Hex(input));
    }

    [Fact]
    public void Fnv1A64Hex_Is16CharLowercase()
    {
        var hex = HashUtil.Fnv1A64Hex("clipboard");
        Assert.Equal(16, hex.Length);
        Assert.Equal(hex.ToLowerInvariant(), hex);
    }

    [Fact]
    public void Fnv1A64_DeterministicAndDistinct()
    {
        Assert.Equal(HashUtil.Fnv1A64("same"), HashUtil.Fnv1A64("same"));
        Assert.NotEqual(HashUtil.Fnv1A64("a"), HashUtil.Fnv1A64("b"));
    }

    [Fact]
    public void Fnv1A64_UnicodeDeterministic()
    {
        Assert.Equal(HashUtil.Fnv1A64("中文剪贴板"), HashUtil.Fnv1A64("中文剪贴板"));
    }
}
