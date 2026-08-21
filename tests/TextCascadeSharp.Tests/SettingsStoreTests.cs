using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

public class SettingsStoreTests
{
    private static string TempPath()
    {
        return Path.Combine(Path.GetTempPath(), "TextCascadeTests", Guid.NewGuid().ToString("N"), "settings.json");
    }

    [Fact]
    public void Defaults_MatchContract()
    {
        var store = new SettingsStore(TempPath(), new SettingsData());
        var data = store.Data;
        Assert.Equal("https://your-server:8443", data.ServerUrl);
        Assert.Equal(ClipConfig.DefaultMaxTextBytes, data.MaxTextBytes);
        Assert.Equal(ClipConfig.DefaultMaxTextBytes, data.LocalMaxClipboardBytes);
        Assert.Equal(ClipConfig.DefaultHashRounds, data.HashRounds);
        Assert.Equal(0UL, data.LastServerVersion);
        Assert.True(data.CipherEnabled);
        Assert.Empty(data.AuthToken);
        Assert.Empty(data.SavedPassword);
        Assert.Empty(data.DerivedKeyBase64);
    }

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var path = TempPath();
        var store = new SettingsStore(path, new SettingsData());
        store.Data.ServerUrl = "https://srv.example.com";
        store.Data.Username = "alice";
        store.Data.AuthToken = "tok";
        store.Data.TokenExpiresAtUtc = "2026-12-31T23:59:59.000Z";
        store.Data.ProtocolVersion = 1;
        store.Data.MaxTextBytes = 777;
        store.Data.HelloTimeoutSeconds = 12;
        store.Data.HeartbeatIntervalSeconds = 23;
        store.Data.HeartbeatTimeoutSeconds = 34;
        store.Data.ClientId = "uuid-x";
        store.Data.ClientName = "PC-1";
        store.Data.LastServerVersion = 99UL;
        store.Data.Salt = "s";
        store.Data.TrustAllCertificates = true;
        store.Save();

        var loaded = SettingsStore.LoadFromPath(path);
        Assert.Null(loaded.LoadError);
        Assert.Equal("https://srv.example.com", loaded.Data.ServerUrl);
        Assert.Equal("alice", loaded.Data.Username);
        Assert.Equal("tok", loaded.Data.AuthToken);
        Assert.Equal("2026-12-31T23:59:59.000Z", loaded.Data.TokenExpiresAtUtc);
        Assert.Equal(1, loaded.Data.ProtocolVersion);
        Assert.Equal(777L, loaded.Data.MaxTextBytes);
        Assert.Equal(12, loaded.Data.HelloTimeoutSeconds);
        Assert.Equal(23, loaded.Data.HeartbeatIntervalSeconds);
        Assert.Equal(34, loaded.Data.HeartbeatTimeoutSeconds);
        Assert.Equal("uuid-x", loaded.Data.ClientId);
        Assert.Equal("PC-1", loaded.Data.ClientName);
        Assert.Equal(99UL, loaded.Data.LastServerVersion);
        Assert.Equal("s", loaded.Data.Salt);
        Assert.True(loaded.Data.TrustAllCertificates);
    }

    [Fact]
    public void Save_UsesAtomicReplaceAndSnakeCaseFields()
    {
        var path = TempPath();
        var store = new SettingsStore(path, new SettingsData());
        store.Save();
        var content = File.ReadAllText(path);
        Assert.Contains("\"server_url\"", content);
        Assert.Contains("\"auth_token\"", content);
        Assert.Contains("\"derived_key_b64\"", content);
        Assert.Contains("\"last_server_version\"", content);
        Assert.Contains("\"trust_all_certificates\"", content);
        // 临时文件已被移动
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void NormalizeServerUrl_TrimsAndFallsBack()
    {
        Assert.Equal("https://srv", SettingsStore.NormalizeServerUrl("  https://srv/ "));
        Assert.Equal("https://your-server:8443", SettingsStore.NormalizeServerUrl("   "));
        Assert.Equal("https://your-server:8443", SettingsStore.NormalizeServerUrl(""));
    }

    [Fact]
    public void ClearSession_ClearsTokenKeepsPersistedSettings()
    {
        var store = new SettingsStore(TempPath(), new SettingsData());
        store.Data.ServerUrl = "https://srv";
        store.Data.Username = "alice";
        store.Data.AuthToken = "tok";
        store.Data.TokenExpiresAtUtc = "2026-12-31T23:59:59.000Z";
        store.Data.ProtocolVersion = 1;
        store.Data.MaxTextBytes = 777;
        store.Data.LastServerVersion = 99UL;
        store.Data.ClientId = "uuid-x";
        store.Data.Salt = "s";
        store.Data.DerivedKeyBase64 = "key";
        store.Data.SavePassword = true;
        store.Data.SavedPassword = "pw";

        store.ClearSession();

        Assert.Empty(store.Data.AuthToken);
        Assert.Empty(store.Data.TokenExpiresAtUtc);
        Assert.Equal(0, store.Data.ProtocolVersion);
        Assert.Equal(ClipConfig.DefaultMaxTextBytes, store.Data.MaxTextBytes);
        Assert.Equal(0UL, store.Data.LastServerVersion);
        // 持久设置保留
        Assert.Equal("https://srv", store.Data.ServerUrl);
        Assert.Equal("alice", store.Data.Username);
        Assert.Equal("uuid-x", store.Data.ClientId);
        Assert.Equal("s", store.Data.Salt);
        Assert.Equal("key", store.Data.DerivedKeyBase64);
        Assert.Equal("pw", store.Data.SavedPassword);
    }

    [Fact]
    public void LoadFromPath_MissingFile_ReturnsDefaults()
    {
        var store = SettingsStore.LoadFromPath(TempPath());
        Assert.Null(store.LoadError);
        Assert.Equal("https://your-server:8443", store.Data.ServerUrl);
    }

    [Fact]
    public void LoadFromPath_CorruptJson_FillsLoadError()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var store = SettingsStore.LoadFromPath(path);
        Assert.NotNull(store.LoadError);
        Assert.Equal("https://your-server:8443", store.Data.ServerUrl);
    }

    [Fact]
    public void LoadFromPath_LegacyFile_IgnoresOldFieldsAndNormalizesDefaults()
    {
        // 旧版（v1.x）设置文件含 csrf/cookie/websocket_url 等旧协议字段；
        // 新客户端应忽略它们并对缺失字段兜底
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "server_url": "https://srv",
              "csrf_token": "x",
              "cookie_header": "JSESSIONID=abc",
              "websocket_url": "wss://srv/clipsocket",
              "hash_rounds": 664937,
              "last_server_version": 5
            }
            """);
        var store = SettingsStore.LoadFromPath(path);
        Assert.Null(store.LoadError);
        Assert.Equal("https://srv", store.Data.ServerUrl);
        Assert.Empty(store.Data.AuthToken);
        Assert.Equal(664_937, store.Data.HashRounds);
        Assert.Equal(5UL, store.Data.LastServerVersion);
        Assert.Equal(ClipConfig.DefaultMaxTextBytes, store.Data.MaxTextBytes);
    }
}
