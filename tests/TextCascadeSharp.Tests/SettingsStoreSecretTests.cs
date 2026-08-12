using System.Text.Json;
using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

public class SettingsStoreSecretTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsStoreSecretTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TextCascadeSecretTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 测试清理失败不影响结论
        }
    }

    private string SettingsPath => Path.Combine(_tempDir, "settings.json");

    [Fact]
    public void Save_Then_File_Contains_Prefixed_Blob()
    {
        var store = new SettingsStore(SettingsPath, new SettingsData
        {
            SavedPassword = "MyP@ssw0rd!",
            CookieHeader = "JSESSIONID=abc",
            CsrfToken = "csrf-token"
        });

        store.Save();
        var json = File.ReadAllText(SettingsPath);

        Assert.Contains("\"saved_password\": \"dpapi:", json, StringComparison.Ordinal);
        Assert.Contains("\"cookie_header\": \"dpapi:", json, StringComparison.Ordinal);
        Assert.Contains("\"csrf_token\": \"dpapi:", json, StringComparison.Ordinal);
        Assert.DoesNotContain("MyP@ssw0rd!", json, StringComparison.Ordinal);
        Assert.DoesNotContain("JSESSIONID=abc", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_Migrates_Legacy_Plaintext_And_Rewrites()
    {
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new SettingsData
        {
            SavedPassword = "legacy-password",
            CookieHeader = "legacy-cookie",
            CsrfToken = "legacy-csrf"
        }));

        var store = SettingsStore.LoadFromPath(SettingsPath);
        var json = File.ReadAllText(SettingsPath);

        Assert.Equal("legacy-password", store.Data.SavedPassword);
        Assert.Equal("legacy-cookie", store.Data.CookieHeader);
        Assert.Equal("legacy-csrf", store.Data.CsrfToken);
        Assert.Contains("\"saved_password\": \"dpapi:", json, StringComparison.Ordinal);
        Assert.Contains("\"cookie_header\": \"dpapi:", json, StringComparison.Ordinal);
        Assert.Contains("\"csrf_token\": \"dpapi:", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_CorruptSecret_ClearsField_And_Sets_LoadError()
    {
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new SettingsData
        {
            SavedPassword = "dpapi:AAAA"
        }));

        var store = SettingsStore.LoadFromPath(SettingsPath);

        Assert.Equal(string.Empty, store.Data.SavedPassword);
        Assert.NotNull(store.LoadError);
        Assert.Contains("decrypted", store.LoadError, StringComparison.OrdinalIgnoreCase);
    }
}
