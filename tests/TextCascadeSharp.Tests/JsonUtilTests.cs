using System.Text.Json;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

// 协议消息序列化/解析与契约样本一致性测试：
// 字段名、紧凑格式（无空白）、Z 结尾时间。
public class JsonUtilTests
{
    [Fact]
    public void LoginRequest_SerializesExactContractFields()
    {
        var json = JsonUtil.LoginRequest("alice", "p@ss wörld");
        Assert.Equal("""{"username":"alice","password":"p@ss wörld"}""", json);
    }

    [Fact]
    public void Hello_WithSnapshot_MatchesContractShape()
    {
        var json = JsonUtil.Hello(new HelloMessage(
            "3fa85f64-5717-4562-b3fc-2c963f66afa6",
            "Windows-Desktop",
            7,
            new HelloSnapshot("hello", false, "af63dc4c8601ec8c", "2026-08-18T08:00:00Z")));
        // 紧凑格式 + 字段顺序与契约样本一致
        Assert.Equal(ContractSamples.HelloWithSnapshot, json);
    }

    [Fact]
    public void Hello_WithoutSnapshot_OmitsSnapshotField()
    {
        var json = JsonUtil.Hello(new HelloMessage("3fa85f64-5717-4562-b3fc-2c963f66afa6", "Windows-Desktop", 0, null));
        Assert.Equal(ContractSamples.HelloWithoutSnapshot, json);
        Assert.DoesNotContain("snapshot", json);
    }

    [Fact]
    public void Clip_SerializesExactContractFields()
    {
        var json = JsonUtil.Clip(new OutboundClipMessage("clip-0001", "hello", false, "af63dc4c8601ec8c"));
        Assert.Equal("""{"type":"clip","id":"clip-0001","payload":"hello","encrypted":false,"hash":"af63dc4c8601ec8c"}""", json);
    }

    [Fact]
    public void Pong_HasZSuffixedClientTime()
    {
        // §5.5 示例：无毫秒 Z 格式
        var json = JsonUtil.Pong(new PongMessage("2026-08-18T08:02:00Z"));
        Assert.Equal(ContractSamples.Pong, json);
        // 运行时生成的时间也必须 Z 结尾（毫秒格式同样合法）
        Assert.EndsWith("Z", JsonUtil.Rfc3339Utc(DateTime.UtcNow));
    }

    [Fact]
    public void MessageTypeOf_ReadsTypeField()
    {
        Assert.Equal("welcome", JsonUtil.MessageTypeOf(ContractSamples.WelcomeEmpty));
        Assert.Equal("clip", JsonUtil.MessageTypeOf(ContractSamples.ClipBroadcast));
        Assert.Equal("clip_ack", JsonUtil.MessageTypeOf(ContractSamples.ClipAck));
        Assert.Equal("ping", JsonUtil.MessageTypeOf(ContractSamples.Ping));
        Assert.Equal("bye", JsonUtil.MessageTypeOf(ContractSamples.Bye));
        Assert.Equal("error", JsonUtil.MessageTypeOf(ContractSamples.ErrorTextTooLarge));
    }

    [Fact]
    public void MessageTypeOf_ReturnsNullForMalformedInput()
    {
        Assert.Null(JsonUtil.MessageTypeOf("not json"));
        Assert.Null(JsonUtil.MessageTypeOf("""{"no_type":1}"""));
        Assert.Null(JsonUtil.MessageTypeOf("""[1,2]"""));
        Assert.Null(JsonUtil.MessageTypeOf("""{"type":42}"""));
    }

    [Fact]
    public void ParseWelcome_LatestNull()
    {
        var welcome = JsonUtil.ParseWelcome(ContractSamples.WelcomeEmpty);
        Assert.Null(welcome.Latest);
    }

    [Fact]
    public void ParseWelcome_LatestPresent_MatchesContract()
    {
        var welcome = JsonUtil.ParseWelcome(ContractSamples.WelcomeWithLatest);
        Assert.NotNull(welcome.Latest);
        Assert.Equal(9UL, welcome.Latest!.Version);
        Assert.Equal("hello", welcome.Latest.Payload);
        Assert.False(welcome.Latest.Encrypted);
        Assert.Equal("af63dc4c8601ec8c", welcome.Latest.Hash);
        Assert.Equal("android-a", welcome.Latest.FromClientId);
        Assert.Equal("2026-08-18T07:59:58Z", welcome.Latest.UpdatedAtUtc);
    }

    [Fact]
    public void ParseWelcome_MissingLatest_TreatedAsNull()
    {
        var welcome = JsonUtil.ParseWelcome("""{"type":"welcome"}""");
        Assert.Null(welcome.Latest);
    }

    [Fact]
    public void ParseClip_MatchesContract()
    {
        var clip = JsonUtil.ParseClip(ContractSamples.ClipBroadcast);
        Assert.Equal(10UL, clip.Version);
        Assert.Equal("world", clip.Payload);
        Assert.False(clip.Encrypted);
        Assert.Equal("3d58dee72d4e0c97", clip.Hash);
    }

    [Fact]
    public void ParseClip_MissingRequiredField_Throws()
    {
        Assert.Throws<JsonException>(() => JsonUtil.ParseClip("""{"type":"clip","payload":"x","encrypted":false}"""));
        Assert.Throws<JsonException>(() => JsonUtil.ParseClip("""{"type":"clip","version":-1,"payload":"x","encrypted":false,"hash":"h"}"""));
    }

    [Fact]
    public void ParseClipAck_MatchesContract()
    {
        var ack = JsonUtil.ParseClipAck(ContractSamples.ClipAck);
        Assert.Equal("clip-0001", ack.Id);
        Assert.Equal(11UL, ack.Version);
    }

    [Fact]
    public void ParsePing_MatchesContract()
    {
        var ping = JsonUtil.ParsePing(ContractSamples.Ping);
        Assert.Equal("2026-08-18T08:02:00Z", ping.ServerTimeUtc);
    }

    [Fact]
    public void ParseBye_MatchesContract()
    {
        var bye = JsonUtil.ParseBye(ContractSamples.Bye);
        Assert.Equal("server_shutdown", bye.Reason);
    }

    [Fact]
    public void ParseError_MatchesContract()
    {
        var error = JsonUtil.ParseError(ContractSamples.ErrorTextTooLarge);
        Assert.Equal("text_too_large", error.Code);
        Assert.Equal("Text exceeds maxTextBytes.", error.Message);
        Assert.Equal("clip-0001", error.ReferenceId);
    }

    [Fact]
    public void EncryptedPayload_RoundTripCompact()
    {
        var payload = new EncryptedPayload("nw==", "cw==", "tg==");
        var json = JsonUtil.EncryptedPayload(payload);
        Assert.Equal("""{"nonce":"nw==","ciphertext":"cw==","tag":"tg=="}""", json);
        var parsed = JsonUtil.ParseEncryptedPayload(json);
        Assert.Equal(payload, parsed);
    }

    [Fact]
    public void Rfc3339Utc_FormatAndRoundTrip()
    {
        // 整秒输出（与服务端契约示例一致，§5.2/§5.5 均为无毫秒 Z 格式）
        var utc = new DateTime(2026, 8, 17, 8, 0, 0, 123, DateTimeKind.Utc);
        var text = JsonUtil.Rfc3339Utc(utc);
        Assert.Equal("2026-08-17T08:00:00Z", text);
        var parsed = JsonUtil.ParseRfc3339Utc(text);
        Assert.NotNull(parsed);
        Assert.Equal(utc.AddMilliseconds(-123), parsed.Value);
    }

    [Fact]
    public void ParseRfc3339Utc_HandlesOffsetAndInvalid()
    {
        // 带偏移的时间归一化为 UTC
        var offsetParsed = JsonUtil.ParseRfc3339Utc("2026-08-17T16:00:00+08:00");
        Assert.Equal(new DateTime(2026, 8, 17, 8, 0, 0, DateTimeKind.Utc), offsetParsed);
        Assert.Null(JsonUtil.ParseRfc3339Utc(null));
        Assert.Null(JsonUtil.ParseRfc3339Utc(""));
        Assert.Null(JsonUtil.ParseRfc3339Utc("not a date"));
    }

    [Fact]
    public void LongField_And_StringField()
    {
        const string json = """{"n":42,"s":"str","b":true}""";
        Assert.Equal(42L, JsonUtil.LongField(json, "n", 0));
        Assert.Equal(-1L, JsonUtil.LongField(json, "missing", -1));
        Assert.Equal(-1L, JsonUtil.LongField(json, "b", -1));
        Assert.Equal("str", JsonUtil.StringField(json, "s"));
        Assert.Equal("", JsonUtil.StringField(json, "missing"));
    }
}
