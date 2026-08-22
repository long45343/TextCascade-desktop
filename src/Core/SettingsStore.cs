using System.Text.Json;

namespace TextCascadeSharp.Core;

// 管理本地 settings.json 的读写。
// 文件位置：%APPDATA%/TextCascade/settings.json（即 Roaming 目录下）。
// 敏感字段（saved_password/auth_token/derived_key_b64）经 DPAPI 保护。
public sealed class SettingsStore
{
    // 缩进输出便于用户阅读和手动编辑。PropertyNamingPolicy=null 表示
    // 按属性原名输出，但实际字段名由 SettingsData 的 [JsonPropertyName] 决定。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public SettingsStore(string filePath, SettingsData data)
    {
        FilePath = filePath;
        Data = data;
        LoadError = null;
    }

    public string FilePath { get; }

    public SettingsData Data { get; }

    // 当 settings.json 存在但无法解析时，记录错误信息。UI 层在 Idle 阶段
    // 通过状态栏向用户提示，避免静默重置。
    public string? LoadError { get; private set; }

    // 从磁盘加载 settings.json。文件不存在时返回默认配置；
    // 文件存在但解析失败时返回默认配置并填充 LoadError。
    public static SettingsStore LoadDefault()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "TextCascade");
        var filePath = Path.Combine(directory, "settings.json");
        return LoadFromPath(filePath);
    }

    // 从指定路径加载（测试用）；生产路径仍走 LoadDefault 的 %APPDATA% 目录
    internal static SettingsStore LoadFromPath(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new SettingsStore(filePath, new SettingsData());
        }

        try
        {
            SettingsData data;
            using (var stream = File.OpenRead(filePath))
            {
                data = JsonSerializer.Deserialize<SettingsData>(stream, JsonOptions) ?? new SettingsData();
            }
            Normalize(data);
            var store = new SettingsStore(filePath, data);
            var needsSecretsRewrite = false;
            var secretErrors = new List<string>();
            var savedPassword = data.SavedPassword;
            var authToken = data.AuthToken;
            var derivedKey = data.DerivedKeyBase64;
            DecodeSecret(ref savedPassword, ref needsSecretsRewrite, secretErrors);
            DecodeSecret(ref authToken, ref needsSecretsRewrite, secretErrors);
            DecodeSecret(ref derivedKey, ref needsSecretsRewrite, secretErrors);
            data.SavedPassword = savedPassword;
            data.AuthToken = authToken;
            data.DerivedKeyBase64 = derivedKey;
            if (needsSecretsRewrite)
            {
                // 迁移/清空后立即落盘；失败静默，等下次 Save 再重试
                try
                {
                    store.Save();
                }
                catch
                {
                }
            }
            if (secretErrors.Count > 0)
            {
                store.LoadError = string.Join("; ", secretErrors);
            }
            return store;
        }
        catch (Exception error)
        {
            // 解析失败时仍返回默认配置，使应用保持可用；
            // 错误信息通过 LoadError 透出给 UI 提示用户。
            var fallback = new SettingsData();
            return new SettingsStore(filePath, fallback) { LoadError = error.Message };
        }
    }

    // 把内存中的设置写回磁盘。使用 先写临时文件再 File.Move(overwrite:true)
    // 的模式以保证写入是原子的：进程在写入中途崩溃不会损坏原文件。
    public void Save()
    {
        Normalize(Data);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tempPath = FilePath + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            // 仅序列化边界加密敏感字段，内存中的 Data 始终保持明文
            var copy = Data.ShallowCopy();
            copy.SavedPassword = SecretProtector.Protect(copy.SavedPassword);
            copy.AuthToken = SecretProtector.Protect(copy.AuthToken);
            copy.DerivedKeyBase64 = SecretProtector.Protect(copy.DerivedKeyBase64);
            JsonSerializer.Serialize(stream, copy, JsonOptions);
        }
        File.Move(tempPath, FilePath, overwrite: true);
    }

    // 解密一个敏感字段。无 dpapi: 前缀视为存量明文并标记重写；
    // 有前缀但解密失败则清空字段并记录 LoadError。
    private static void DecodeSecret(ref string value, ref bool needsRewrite, List<string> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        if (!SecretProtector.IsProtected(value))
        {
            needsRewrite = true;
            return;
        }
        if (SecretProtector.TryUnprotect(value, out var plaintext))
        {
            value = plaintext;
            return;
        }
        Logger.LogError("Failed to unprotect stored secret; field cleared.");
        value = string.Empty;
        needsRewrite = true;
        errors.Add("A saved credential could not be decrypted and was cleared.");
    }

    // 注销后清除会话凭据与 token（保留服务器地址、用户名、加密参数、
    // 派生密钥、保存的密码等持久设置）
    public void ClearSession()
    {
        Data.AuthToken = string.Empty;
        Data.TokenExpiresAtUtc = string.Empty;
        Data.ProtocolVersion = 0;
        Data.MaxTextBytes = ClipConfig.DefaultMaxTextBytes;
        Data.HelloTimeoutSeconds = ClipConfig.DefaultHelloTimeoutSeconds;
        Data.HeartbeatIntervalSeconds = ClipConfig.DefaultHeartbeatIntervalSeconds;
        Data.HeartbeatTimeoutSeconds = ClipConfig.DefaultHeartbeatTimeoutSeconds;
        Data.LastServerVersion = 0;
    }

    // 标准化服务器 URL：去空格、去尾斜杠、空则回退到占位默认值
    public static string NormalizeServerUrl(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? "https://your-server:8443" : normalized;
    }

    // 把加载或保存前的 SettingsData 修正为合法值。
    // 配置文件可能缺失某些字段或为 0，这里统一兜底。
    public static string NormalizeThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return string.Empty;
        }
        return thumbprint.Trim().Replace(":", string.Empty).ToUpperInvariant();
    }

    public static void NormalizeData(SettingsData data) => Normalize(data);

    internal static void Normalize(SettingsData data)
    {
        data.ServerUrl = NormalizeServerUrl(data.ServerUrl);
        data.Username = data.Username.Trim();
        data.ServerCertificateThumbprint = NormalizeThumbprint(data.ServerCertificateThumbprint);
        if (data.MaxTextBytes <= 0)
        {
            data.MaxTextBytes = ClipConfig.DefaultMaxTextBytes;
        }
        if (data.LocalMaxClipboardBytes <= 0)
        {
            data.LocalMaxClipboardBytes = ClipConfig.DefaultMaxTextBytes;
        }
        if (data.HashRounds <= 0)
        {
            data.HashRounds = ClipConfig.DefaultHashRounds;
        }
        if (data.HelloTimeoutSeconds <= 0)
        {
            data.HelloTimeoutSeconds = ClipConfig.DefaultHelloTimeoutSeconds;
        }
        if (data.HeartbeatIntervalSeconds <= 0)
        {
            data.HeartbeatIntervalSeconds = ClipConfig.DefaultHeartbeatIntervalSeconds;
        }
        if (data.HeartbeatTimeoutSeconds <= 0)
        {
            data.HeartbeatTimeoutSeconds = ClipConfig.DefaultHeartbeatTimeoutSeconds;
        }
        if (string.IsNullOrWhiteSpace(data.ClientName))
        {
            data.ClientName = Environment.MachineName;
        }
    }
}
