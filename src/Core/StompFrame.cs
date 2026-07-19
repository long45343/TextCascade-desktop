using System.Text;

namespace TextCascadeSharp.Core;

// STOMP 帧的序列化/反序列化。
// 帧格式（STOMP 1.1+）：
//   COMMAND\n
//   key1:value1\n
//   key2:value2\n
//   \n
//   body\0
// 头部 key 与 value 都需要按 STOMP 1.1 转义规则处理：
//   \\ -> \, \r -> CR, \n -> LF, \c -> :
// 参考：https://stomp.github.io/stomp-specification-1.1.html#Repeated_and_Escaped_Headers
internal sealed record StompFrame(string Command, IReadOnlyDictionary<string, string> Headers, string Body)
{
    // 把帧序列化为字符串（不含 \0 之外的特殊处理）。
    // content-length 头由本方法自动追加，表示 body 的 UTF-8 字节数。
    public string Marshall()
    {
        var builder = new StringBuilder(Command.Length + Body.Length + 64);
        builder.Append(Command).Append('\n');
        foreach (var pair in Headers)
        {
            // 跳过调用方可能误传的 content-length，统一由本方法管理
            if (pair.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            builder.Append(EscapeHeader(pair.Key)).Append(':').Append(EscapeHeader(pair.Value)).Append('\n');
        }
        // content-length 帮助接收方精确知道 body 长度（即使 body 含 \0）
        builder.Append("content-length:").Append(Encoding.UTF8.GetByteCount(Body)).Append('\n');
        builder.Append('\n').Append(Body).Append('\0');
        return builder.ToString();
    }

    // 解析一个不含 \0 结束符的原始帧字符串。
    // 兼容 STOMP 1.0（无转义）和 1.1+（有转义）。
    public static StompFrame Parse(string raw)
    {
        // 统一换行为 \n
        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        // 头部与 body 之间以空行分隔
        var separator = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var headerPart = separator >= 0 ? normalized[..separator] : normalized;
        var rawBody = separator >= 0 ? normalized[(separator + 2)..] : string.Empty;
        var lines = headerPart.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 第 0 行是 COMMAND，从第 1 行开始是 header
        for (var index = 1; index < lines.Length; index++)
        {
            var colon = lines[index].IndexOf(':');
            if (colon > 0)
            {
                // STOMP 1.1+ 头部转义解码。
                // 若输入来自 1.0 客户端（无转义），UnescapeHeader 会原样返回。
                var key = UnescapeHeader(lines[index][..colon]);
                var value = UnescapeHeader(lines[index][(colon + 1)..]);
                headers[key] = value;
            }
        }

        // 若有 content-length，按字节长度截取 body。
        // 没有时整个 rawBody 都是 body。
        var body = rawBody;
        if (headers.TryGetValue("content-length", out var contentLength) && int.TryParse(contentLength, out var length))
        {
            var bytes = Encoding.UTF8.GetBytes(rawBody);
            body = Encoding.UTF8.GetString(bytes, 0, Math.Min(length, bytes.Length));
        }
        return new StompFrame(lines.Length > 0 ? lines[0].Trim() : string.Empty, headers, body);
    }

    // STOMP 1.1+ 头部转义。
    // 仅在值含 \\、\r、\n、: 时才进行转义，避免无谓的字符串分配
    private static string EscapeHeader(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOfAny(['\\', '\r', '\n', ':']) < 0)
        {
            return value;
        }
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case ':': builder.Append("\\c"); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }

    // STOMP 1.1+ 头部反转义。
    // 若输入不含 \ 字符（即来自 STOMP 1.0 客户端），直接原样返回。
    private static string UnescapeHeader(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
        {
            return value;
        }
        var builder = new StringBuilder(value.Length);
        var index = 0;
        while (index < value.Length)
        {
            var c = value[index];
            if (c == '\\' && index + 1 < value.Length)
            {
                var next = value[index + 1];
                switch (next)
                {
                    case '\\': builder.Append('\\'); break;
                    case 'r': builder.Append('\r'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'c': builder.Append(':'); break;
                    default: builder.Append(next); break;
                }
                index += 2;
            }
            else
            {
                builder.Append(c);
                index++;
            }
        }
        return builder.ToString();
    }
}
