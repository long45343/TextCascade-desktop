using System.Text.Json;

namespace TextCascadeSharp.Core;

// 管理本地 settings.json 的读写。
// 文件位置：%APPDATA%/TextCascade/settings.json（即 Roaming 目录下）
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
    // 通过状态栏向用户提示，避免静默重置（review issue #16）。
    public string? LoadError { get; private set; }

    // 从磁盘加载 settings.json。文件不存在时返回默认配置；
    // 文件存在但解析失败时返回默认配置并填充 LoadError。
    public static SettingsStore LoadDefault()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "TextCascade");
        var filePath = Path.Combine(directory, "settings.json");
        if (!File.Exists(filePath))
        {
            return new SettingsStore(filePath, new SettingsData());
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            var data = JsonSerializer.Deserialize<SettingsData>(stream, JsonOptions) ?? new SettingsData();
            Normalize(data);
            return new SettingsStore(filePath, data);
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
            JsonSerializer.Serialize(stream, Data, JsonOptions);
        }
        File.Move(tempPath, FilePath, overwrite: true);
    }

    // 注销后清除所有会话凭据（保留服务器地址、用户名、加密参数等持久设置）
    public void ClearSession()
    {
        Data.WebsocketUrl = string.Empty;
        Data.PasswordSha3 = string.Empty;
        Data.HashedPasswordBase64 = string.Empty;
        Data.CsrfToken = string.Empty;
        Data.CookieHeader = string.Empty;
    }

    // 标准化服务器 URL：去空格、去尾斜杠、空则回退到 localhost:8080
    public static string NormalizeServerUrl(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? "http://localhost:8080" : normalized;
    }

    // 把加载或保存前的 SettingsData 修正为合法值。
    // 旧版本配置文件可能缺失某些字段或为 0，这里统一兜底。
    private static void Normalize(SettingsData data)
    {
        data.ServerUrl = NormalizeServerUrl(data.ServerUrl);
        data.Username = data.Username.Trim();
        if (data.MaxSizeBytes <= 0)
        {
            data.MaxSizeBytes = ClipConfig.DefaultMaxSizeBytes;
        }
        if (data.LocalMaxClipboardBytes <= 0)
        {
            data.LocalMaxClipboardBytes = ClipConfig.DefaultMaxSizeBytes;
        }
        if (data.HashRounds <= 0)
        {
            data.HashRounds = ClipConfig.DefaultHashRounds;
        }
    }
}
