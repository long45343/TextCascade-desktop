using System.Text;

namespace TextCascadeSharp.Core;

internal sealed record StompFrame(string Command, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string Marshall()
    {
        var builder = new StringBuilder(Command.Length + Body.Length + 64);
        builder.Append(Command).Append('\n');
        foreach (var pair in Headers)
        {
            if (pair.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            builder.Append(pair.Key).Append(':').Append(pair.Value).Append('\n');
        }
        builder.Append("content-length:").Append(Encoding.UTF8.GetByteCount(Body)).Append('\n');
        builder.Append('\n').Append(Body).Append('\0');
        return builder.ToString();
    }

    public static StompFrame Parse(string raw)
    {
        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        var separator = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var headerPart = separator >= 0 ? normalized[..separator] : normalized;
        var rawBody = separator >= 0 ? normalized[(separator + 2)..] : string.Empty;
        var lines = headerPart.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length; index++)
        {
            var colon = lines[index].IndexOf(':');
            if (colon > 0)
            {
                headers[lines[index][..colon]] = lines[index][(colon + 1)..];
            }
        }

        var body = rawBody;
        if (headers.TryGetValue("content-length", out var value) && int.TryParse(value, out var length))
        {
            var bytes = Encoding.UTF8.GetBytes(rawBody);
            body = Encoding.UTF8.GetString(bytes, 0, Math.Min(length, bytes.Length));
        }
        return new StompFrame(lines.Length > 0 ? lines[0].Trim() : string.Empty, headers, body);
    }
}
