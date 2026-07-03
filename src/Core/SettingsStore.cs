using System.Text.Json;

namespace TextCascadeSharp.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public SettingsStore(string filePath, SettingsData data)
    {
        FilePath = filePath;
        Data = data;
    }

    public string FilePath { get; }

    public SettingsData Data { get; }

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
        catch
        {
            return new SettingsStore(filePath, new SettingsData());
        }
    }

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

    public void ClearSession()
    {
        Data.WebsocketUrl = string.Empty;
        Data.PasswordSha3 = string.Empty;
        Data.HashedPasswordBase64 = string.Empty;
        Data.CsrfToken = string.Empty;
        Data.CookieHeader = string.Empty;
    }

    public static string NormalizeServerUrl(string value)
    {
        var normalized = value.Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? "http://localhost:8080" : normalized;
    }

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
