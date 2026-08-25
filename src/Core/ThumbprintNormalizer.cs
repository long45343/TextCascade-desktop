namespace TextCascadeSharp.Core;

// 证书 SHA-256 指纹的统一归一化入口：去除冒号、空格、连字符并转大写，
// 兼容 Windows 证书详情里的多种复制格式。
internal static class ThumbprintNormalizer
{
    public static string Normalize(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return string.Empty;
        }
        return thumbprint.Replace(":", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Trim()
            .ToUpperInvariant();
    }
}
