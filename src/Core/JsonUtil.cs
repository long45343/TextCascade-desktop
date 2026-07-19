using System.Text.Json;

namespace TextCascadeSharp.Core;

// STOMP MESSAGE 帧的 body 与 /app/cliptext SEND 帧的 body 共用同一套 JSON 结构。
// 本类统一封装序列化/反序列化，避免各处重复 JsonSerializerOptions。
public static class JsonUtil
{
    // PropertyNamingPolicy=null 表示按属性原名输出（snake_case 来自 JsonPropertyName 特性）
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null
    };

    // 构造出站剪贴板消息 JSON。type 默认 "text"，与各端约定一致
    public static string ClipMessage(string payload, string type = "text")
    {
        return JsonSerializer.Serialize(new ClipMessage(payload, type), Options);
    }

    // 解析入站 STOMP MESSAGE 帧的 body
    public static ClipMessage ParseClipMessage(string json)
    {
        return JsonSerializer.Deserialize<ClipMessage>(json, Options)
            ?? throw new JsonException("Empty clip message.");
    }

    // 把 EncryptedPayload 序列化为 JSON 字符串，作为 ClipMessage.Payload 字段
    public static string EncryptedPayload(EncryptedPayload payload)
    {
        return JsonSerializer.Serialize(payload, Options);
    }

    // 把 ClipMessage.Payload 字段解析回 EncryptedPayload
    public static EncryptedPayload ParseEncryptedPayload(string json)
    {
        return JsonSerializer.Deserialize<EncryptedPayload>(json, Options)
            ?? throw new JsonException("Empty encrypted payload.");
    }

    // 从任意 JSON 中按字段名读取 long，缺失或类型不符时返回默认值。
    // 用于解析登录响应等只取单字段的场景，避免为每个响应建一个 record。
    // 注意：JsonElement.TryGetInt64 在 ValueKind 不是 Number 时会抛
    // InvalidOperationException，因此必须先显式检查 ValueKind。
    public static long LongField(string json, string name, long defaultValue)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(name, out var value))
        {
            return defaultValue;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed
            : defaultValue;
    }

    // 从任意 JSON 中按字段名读取 string
    public static string StringField(string json, string name, string defaultValue = "")
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? defaultValue
            : defaultValue;
    }
}
