using System.Security.Cryptography;
using System.Text;

namespace TextCascadeSharp.Core;

// DPAPI（CurrentUser 作用域）保护落盘敏感字段。
// 密文格式："dpapi:" + Base64(blob)。无前缀视为存量明文（迁移路径）。
internal static class SecretProtector
{
    private const string Prefix = "dpapi:";
    // 固定 entropy：不追求机密性，只防止跨应用直接 Unprotect
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TextCascade.v1");

    public static bool IsProtected(string stored) => stored.StartsWith(Prefix, StringComparison.Ordinal);

    // 空串→空串；否则 Prefix + Base64(protected)
    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }
        var input = Encoding.UTF8.GetBytes(plaintext);
        var output = ProtectedData.Protect(input, Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(output);
    }

    // 无前缀→原样返回（存量明文迁移）；失败抛 CryptographicException
    public static string Unprotect(string stored)
    {
        if (!IsProtected(stored))
        {
            return stored;
        }
        var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
        var output = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(output);
    }

    // 迁移友好版：失败返回 false 且不抛，value 置空
    public static bool TryUnprotect(string stored, out string value)
    {
        value = string.Empty;
        if (!IsProtected(stored))
        {
            value = stored;
            return true;
        }
        try
        {
            value = Unprotect(stored);
            return true;
        }
        catch
        {
            value = string.Empty;
            return false;
        }
    }
}
