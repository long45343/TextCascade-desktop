using System.Text;
using System.Text.Json;

namespace TextCascadeSharp.Core;

// textcascade.v1 消息的 JSON 编解码。
// 上行消息（hello/clip/pong/登录请求）使用 Utf8JsonWriter 手写序列化，
// 保证：紧凑格式、只含契约字段、字段顺序稳定（服务端拒绝未知/重复字段）。
// 下行消息解析保持宽松：允许多余字段，缺失必需字段时抛 JsonException。
public static class JsonUtil
{
    // ---------- 上行序列化 ----------

    // POST /api/v1/login 请求体：{"username":"...","password":"..."}
    public static string LoginRequest(string username, string password)
    {
        return WritePlain(writer =>
        {
            writer.WriteString("username", username);
            writer.WriteString("password", password);
        });
    }

    // {"type":"hello","clientId":"...","clientName":"...","lastServerVersion":N[,"snapshot":{...}}]}
    public static string Hello(HelloMessage message)
    {
        return WritePlain(writer =>
        {
            writer.WriteString("type", "hello");
            writer.WriteString("clientId", message.ClientId);
            writer.WriteString("clientName", message.ClientName);
            writer.WriteNumber("lastServerVersion", message.LastServerVersion);
            if (message.Snapshot is { } snapshot)
            {
                writer.WriteStartObject("snapshot");
                writer.WriteString("payload", snapshot.Payload);
                writer.WriteBoolean("encrypted", snapshot.Encrypted);
                writer.WriteString("hash", snapshot.Hash);
                writer.WriteString("localModifiedAtUtc", snapshot.LocalModifiedAtUtc);
                writer.WriteEndObject();
            }
        });
    }

    // {"type":"clip","id":"...","payload":"...","encrypted":bool,"hash":"..."}
    public static string Clip(OutboundClipMessage message)
    {
        return WritePlain(writer =>
        {
            writer.WriteString("type", "clip");
            writer.WriteString("id", message.Id);
            writer.WriteString("payload", message.Payload);
            writer.WriteBoolean("encrypted", message.Encrypted);
            writer.WriteString("hash", message.Hash);
        });
    }

    // {"type":"pong","clientTimeUtc":"<RFC3339 Z>"}
    public static string Pong(PongMessage message)
    {
        return WritePlain(writer =>
        {
            writer.WriteString("type", "pong");
            writer.WriteString("clientTimeUtc", message.ClientTimeUtc);
        });
    }

    // 把 EncryptedPayload 序列化为紧凑 JSON 字符串，作为 clip/hello 的 payload 字段
    public static string EncryptedPayload(EncryptedPayload payload)
    {
        return WritePlain(writer =>
        {
            writer.WriteString("nonce", payload.Nonce);
            writer.WriteString("ciphertext", payload.Ciphertext);
            writer.WriteString("tag", payload.Tag);
        });
    }

    // ---------- 下行解析 ----------

    // 读取消息的 type 字段；非对象/无 type/类型非字符串返回 null
    public static string? MessageTypeOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            return type.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // {"type":"welcome","protocolVersion":1,"latest":null} 或
    // latest 为 {version,payload,encrypted,hash,fromClientId?,updatedAtUtc?}（§5.3）
    public static WelcomeMessage ParseWelcome(string json)
    {
        var root = ParseRootObject(json, "welcome");
        if (!root.TryGetProperty("latest", out var latest) || latest.ValueKind == JsonValueKind.Null)
        {
            return new WelcomeMessage(null);
        }
        RequireObject(latest, "welcome.latest");
        return new WelcomeMessage(new WelcomeLatest(
            ReadUnsignedNumber(latest, "version", "welcome.latest.version"),
            ReadString(latest, "payload", "welcome.latest.payload"),
            ReadBoolean(latest, "encrypted", "welcome.latest.encrypted"),
            ReadString(latest, "hash", "welcome.latest.hash"),
            TryReadString(latest, "fromClientId"),
            TryReadString(latest, "updatedAtUtc")));
    }

    // {"type":"clip","version":N,"id"?,"payload":"...","encrypted":bool,"hash":"...",
    //  "fromClientId"?,"fromClientName"?,"updatedAtUtc"?}（§5.4）
    public static InboundClipMessage ParseClip(string json)
    {
        var root = ParseRootObject(json, "clip");
        return new InboundClipMessage(
            ReadUnsignedNumber(root, "version", "clip.version"),
            ReadString(root, "payload", "clip.payload"),
            ReadBoolean(root, "encrypted", "clip.encrypted"),
            ReadString(root, "hash", "clip.hash"),
            TryReadString(root, "id"),
            TryReadString(root, "fromClientId"),
            TryReadString(root, "fromClientName"),
            TryReadString(root, "updatedAtUtc"));
    }

    // {"type":"clip_ack","id":"...","version":N,"updatedAtUtc"?}（§5.4）
    public static ClipAckMessage ParseClipAck(string json)
    {
        var root = ParseRootObject(json, "clip_ack");
        return new ClipAckMessage(
            ReadString(root, "id", "clip_ack.id"),
            ReadUnsignedNumber(root, "version", "clip_ack.version"),
            TryReadString(root, "updatedAtUtc"));
    }

    // {"type":"ping","serverTimeUtc":"..."}
    public static PingMessage ParsePing(string json)
    {
        var root = ParseRootObject(json, "ping");
        return new PingMessage(TryReadString(root, "serverTimeUtc"));
    }

    // {"type":"bye","reason":"..."?}
    public static ByeMessage ParseBye(string json)
    {
        var root = ParseRootObject(json, "bye");
        return new ByeMessage(TryReadString(root, "reason"));
    }

    // {"type":"error","code":"...","message":"..."?,"referenceId":"..."?}（§5.6）
    public static ErrorMessage ParseError(string json)
    {
        var root = ParseRootObject(json, "error");
        return new ErrorMessage(
            ReadString(root, "code", "error.code"),
            TryReadString(root, "message"),
            TryReadString(root, "referenceId"));
    }

    // 把 clip/hello 的 payload 字段解析回 EncryptedPayload
    public static EncryptedPayload ParseEncryptedPayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Encrypted payload must be a JSON object.");
        }
        return new EncryptedPayload(
            ReadString(root, "nonce", "payload.nonce"),
            ReadString(root, "ciphertext", "payload.ciphertext"),
            ReadString(root, "tag", "payload.tag"));
    }

    // ---------- 通用字段读取 ----------

    // 从任意 JSON 中按字段名读取 long，缺失或类型不符时返回默认值
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

    // ---------- RFC3339 UTC 时间 ----------

    // 输出形如 2026-08-17T12:34:56Z（整秒、Z 结尾）。
    // 与服务端契约示例一致；整秒格式可避免对端对 localModifiedAtUtc
    // 做字符串比较时同秒内的排序歧义（§6.2 选举规则 4）
    public static string Rfc3339Utc(DateTime utc)
    {
        var value = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Rfc3339UtcNow(TimeProvider? timeProvider = null)
    {
        return Rfc3339Utc((timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
    }

    // 解析 RFC3339 时间；无法解析返回 null
    public static DateTime? ParseRfc3339Utc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return null;
        }
        return parsed.Kind switch
        {
            DateTimeKind.Utc => parsed,
            DateTimeKind.Local => parsed.ToUniversalTime(),
            _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
        };
    }

    // ---------- 私有辅助 ----------

    private static string WritePlain(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        var options = new JsonWriterOptions
        {
            // 剪贴板文本常含中文等非 ASCII 字符：按原始 UTF-8 输出，
            // 不做 \uXXXX 转义（保证 payload 字节数与内容一致）
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonElement ParseRootObject(string json, string typeName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.Clone();
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"'{typeName}' message must be a JSON object.");
        }
        return root;
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"'{name}' must be a JSON object.");
        }
    }

    private static string ReadString(JsonElement parent, string name, string fullName)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Missing or invalid string field '{fullName}'.");
        }
        return value.GetString()!;
    }

    private static string? TryReadString(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool ReadBoolean(JsonElement parent, string name, string fullName)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False))
        {
            throw new JsonException($"Missing or invalid boolean field '{fullName}'.");
        }
        return value.GetBoolean();
    }

    private static ulong ReadUnsignedNumber(JsonElement parent, string name, string fullName)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new JsonException($"Missing or invalid number field '{fullName}'.");
        }
        return value.TryGetInt64(out var parsed) && parsed >= 0
            ? (ulong)parsed
            : throw new JsonException($"Field '{fullName}' must be a non-negative integer.");
    }
}
