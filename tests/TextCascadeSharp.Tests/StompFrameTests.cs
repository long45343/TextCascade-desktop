using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

/// <summary>
/// StompFrame 序列化/反序列化测试。
/// 覆盖 STOMP 1.1+ 头部转义规则（review issue #7 修复）：
///   \\ -> \, \r -> CR, \n -> LF, \c -> :
/// 同时验证对 1.0 客户端（无转义）的向后兼容。
/// </summary>
public class StompFrameTests
{
    [Fact]
    public void Marshall_BasicSendFrame_IncludesContentLength()
    {
        var frame = new StompFrame(
            "SEND",
            new Dictionary<string, string> { ["destination"] = "/app/cliptext" },
            "hello");

        var marshalled = frame.Marshall();

        Assert.StartsWith("SEND\n", marshalled);
        Assert.Contains("destination:/app/cliptext\n", marshalled);
        Assert.Contains("content-length:5\n", marshalled); // "hello" = 5 字节
        Assert.EndsWith("\nhello\0", marshalled);
    }

    [Fact]
    public void Marshall_HeaderWithColon_IsEscaped()
    {
        var frame = new StompFrame(
            "SEND",
            new Dictionary<string, string> { ["x-meta"] = "a:b" },
            "");

        var marshalled = frame.Marshall();

        Assert.Contains("x-meta:a\\cb\n", marshalled);
    }

    [Fact]
    public void Marshall_HeaderWithBackslash_IsEscaped()
    {
        // header 值是字面字符串 path\to（4 字符 + 1 反斜杠 = 7 字符）
        // marshalled 后反斜杠被转义为 \\，输出里是 path\\to（含 2 个反斜杠）
        var frame = new StompFrame(
            "SEND",
            new Dictionary<string, string> { ["x-meta"] = @"path\to" },
            "");

        // 期望输出含字面 "x-meta:path\\to\n"（即字符串里 2 个反斜杠）
        Assert.Contains(@"x-meta:path\\to" + "\n", frame.Marshall());
    }

    [Fact]
    public void Parse_BasicMessageFrame_ExtractsCommandHeadersBody()
    {
        var raw = "MESSAGE\ndestination:/queue/x\ncontent-length:5\n\nhello";

        var frame = StompFrame.Parse(raw);

        Assert.Equal("MESSAGE", frame.Command);
        Assert.Equal("/queue/x", frame.Headers["destination"]);
        Assert.Equal("hello", frame.Body);
    }

    [Fact]
    public void Parse_EscapedHeader_UnescapesColonAndBackslash()
    {
        // STOMP 1.1+ 转义：\c -> :, \\ -> \
        var raw = "MESSAGE\nx-meta:a\\cb\\\\path\n\nbody";

        var frame = StompFrame.Parse(raw);

        Assert.Equal("a:b\\path", frame.Headers["x-meta"]);
    }

    [Fact]
    public void Parse_NoEscapeSequence_PassesThroughUnchanged()
    {
        // 兼容 STOMP 1.0 客户端（无转义）：含 \ 字符的 header 原样返回
        var raw = "MESSAGE\nx-meta:plain\n\nbody";

        var frame = StompFrame.Parse(raw);

        Assert.Equal("plain", frame.Headers["x-meta"]);
    }

    [Fact]
    public void Parse_CRLF_NormalizedToLF()
    {
        // 服务端可能用 \r\n 换行，必须先归一化为 \n
        var raw = "MESSAGE\r\ndestination:/queue/x\r\ncontent-length:5\r\n\r\nhello";

        var frame = StompFrame.Parse(raw);

        Assert.Equal("MESSAGE", frame.Command);
        Assert.Equal("hello", frame.Body);
    }

    [Fact]
    public void RoundTrip_MarshallThenParse_PreservesAllFields()
    {
        var original = new StompFrame(
            "SEND",
            new Dictionary<string, string>
            {
                ["destination"] = "/app/cliptext",
                ["x-meta"] = "value:with:colons"
            },
            "body content with 中文");

        var marshalled = original.Marshall();
        var parsed = StompFrame.Parse(marshalled);

        Assert.Equal(original.Command, parsed.Command);
        Assert.Equal(original.Body, parsed.Body);
        Assert.Equal(original.Headers["destination"], parsed.Headers["destination"]);
        // 转义过后的 header 解析回来应得到原始值
        Assert.Equal(original.Headers["x-meta"], parsed.Headers["x-meta"]);
    }

    [Fact]
    public void Parse_WithContentLength_TruncatesBodyToExactByteCount()
    {
        // 即使 body 后面有额外内容（如多个帧拼接），也按 content-length 截取
        var raw = "MESSAGE\ncontent-length:5\n\nhelloextra";

        var frame = StompFrame.Parse(raw);

        Assert.Equal("hello", frame.Body);
    }

    [Fact]
    public void Parse_EmptyBody_WithContentLengthZero()
    {
        var raw = "CONNECTED\ncontent-length:0\n\n";

        var frame = StompFrame.Parse(raw);

        Assert.Equal(string.Empty, frame.Body);
    }
}
