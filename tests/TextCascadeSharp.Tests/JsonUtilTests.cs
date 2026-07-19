using System.Text.Json;
using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

/// <summary>
/// JsonUtil 序列化测试。
/// 验证 ClipMessage / EncryptedPayload 的字段命名符合服务端约定（snake_case）。
/// </summary>
public class JsonUtilTests
{
    [Fact]
    public void ClipMessage_DefaultType_IsText()
    {
        var json = JsonUtil.ClipMessage("hello");
        Assert.Contains("\"payload\":\"hello\"", json);
        Assert.Contains("\"type\":\"text\"", json);
    }

    [Fact]
    public void ClipMessage_CustomType_Serialized()
    {
        var json = JsonUtil.ClipMessage("data", "custom");
        Assert.Contains("\"type\":\"custom\"", json);
    }

    [Fact]
    public void ParseClipMessage_ValidJson_ReturnsFields()
    {
        var json = """{"payload":"hello","type":"text"}""";

        var msg = JsonUtil.ParseClipMessage(json);

        Assert.Equal("hello", msg.Payload);
        Assert.Equal("text", msg.Type);
    }

    [Fact]
    public void ParseClipMessage_EmptyJson_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => JsonUtil.ParseClipMessage(""));
    }

    [Fact]
    public void EncryptedPayload_RoundTrip()
    {
        var payload = new EncryptedPayload("nonce==", "cipher==", "tag==");
        var json = JsonUtil.EncryptedPayload(payload);

        Assert.Contains("\"nonce\":\"nonce==\"", json);
        Assert.Contains("\"ciphertext\":\"cipher==\"", json);
        Assert.Contains("\"tag\":\"tag==\"", json);

        var parsed = JsonUtil.ParseEncryptedPayload(json);
        Assert.Equal(payload, parsed);
    }

    [Fact]
    public void LongField_Exists_ReturnsValue()
    {
        var json = """{"maxsize": 1024}""";
        Assert.Equal(1024, JsonUtil.LongField(json, "maxsize", 0));
    }

    [Fact]
    public void LongField_Missing_ReturnsDefault()
    {
        var json = """{"other": 1}""";
        Assert.Equal(512, JsonUtil.LongField(json, "maxsize", 512));
    }

    [Fact]
    public void LongField_WrongType_ReturnsDefault()
    {
        var json = """{"maxsize": "not a number"}""";
        Assert.Equal(99, JsonUtil.LongField(json, "maxsize", 99));
    }

    [Fact]
    public void StringField_Exists_ReturnsValue()
    {
        var json = """{"mode": "P2S"}""";
        Assert.Equal("P2S", JsonUtil.StringField(json, "mode", ""));
    }

    [Fact]
    public void StringField_Missing_ReturnsDefault()
    {
        var json = """{"other": "x"}""";
        Assert.Equal("default", JsonUtil.StringField(json, "mode", "default"));
    }
}
