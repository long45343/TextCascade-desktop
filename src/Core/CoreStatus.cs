using System.Text;

namespace TextCascadeSharp.Core;

// Core → UI 状态信封编码助手。保持既有 Action<string> 回调管道不变，
// 将"领域码 + 参数"打包为一个以 Prefix 前缀标记的分隔符字符串；
// UI 层用 TryUnpack 解包并按 ErrorCodes 映射本地化文案。
internal static class CoreStatus
{
    public const string Separator = "\u001f";

    public static string Prefix => "TCSTATUS\u001f";

    // 打包：TCSTATUS\u001f<code>\u001f<arg0>\u001f<arg1>...
    public static string Pack(string code, params object?[] args)
    {
        var sb = new StringBuilder(Prefix).Append(code);
        if (args is not null)
        {
            foreach (var arg in args)
            {
                sb.Append(Separator);
                sb.Append(arg?.ToString() ?? "");
            }
        }
        return sb.ToString();
    }

    // 解包：raw 以 Prefix 开头则解析出 code 与 args（均转字符串），否则返回 false。
    public static bool TryUnpack(string raw, out string code, out string[] args)
    {
        code = "";
        args = [];
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var parts = raw.Substring(Prefix.Length).Split(Separator);
        code = parts[0];
        args = parts.Skip(1).ToArray();
        return true;
    }
}