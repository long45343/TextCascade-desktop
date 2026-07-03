using System.Text.Json;

namespace TextCascadeSharp.Core;

public static class JsonUtil
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null
    };

    public static string ClipMessage(string payload, string type = "text")
    {
        return JsonSerializer.Serialize(new ClipMessage(payload, type), Options);
    }

    public static ClipMessage ParseClipMessage(string json)
    {
        return JsonSerializer.Deserialize<ClipMessage>(json, Options)
            ?? throw new JsonException("Empty clip message.");
    }

    public static string EncryptedPayload(EncryptedPayload payload)
    {
        return JsonSerializer.Serialize(payload, Options);
    }

    public static EncryptedPayload ParseEncryptedPayload(string json)
    {
        return JsonSerializer.Deserialize<EncryptedPayload>(json, Options)
            ?? throw new JsonException("Empty encrypted payload.");
    }

    public static long LongField(string json, string name, long defaultValue)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed)
            ? parsed
            : defaultValue;
    }

    public static string StringField(string json, string name, string defaultValue = "")
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? defaultValue
            : defaultValue;
    }
}
