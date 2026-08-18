using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

// DPAPI 边界加密往返：saved_password / auth_token / derived_key_b64。
public class SettingsStoreSecretTests
{
    private static string TempPath()
    {
        return Path.Combine(Path.GetTempPath(), "TextCascadeTests", Guid.NewGuid().ToString("N"), "settings.json");
    }

    [Fact]
    public void Save_ProtectsAllThreeSecretFields()
    {
        var path = TempPath();
        var store = new SettingsStore(path, new SettingsData());
        store.Data.AuthToken = "tok-secret";
        store.Data.DerivedKeyBase64 = "key-secret";
        store.Data.SavePassword = true;
        store.Data.SavedPassword = "pw-secret";
        store.Save();

        var content = File.ReadAllText(path);
        Assert.DoesNotContain("tok-secret", content);
        Assert.DoesNotContain("key-secret", content);
        Assert.DoesNotContain("pw-secret", content);
        Assert.Contains("dpapi:", content);

        // 重新加载后还原明文
        var loaded = SettingsStore.LoadFromPath(path);
        Assert.Equal("tok-secret", loaded.Data.AuthToken);
        Assert.Equal("key-secret", loaded.Data.DerivedKeyBase64);
        Assert.Equal("pw-secret", loaded.Data.SavedPassword);
    }

    [Fact]
    public void Load_MigratesLegacyPlaintextSecretsAndRewrites()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "server_url": "https://srv",
              "auth_token": "legacy-token",
              "derived_key_b64": "legacy-key",
              "save_password": true,
              "saved_password": "legacy-pw"
            }
            """);
        var store = SettingsStore.LoadFromPath(path);
        Assert.Null(store.LoadError);
        Assert.Equal("legacy-token", store.Data.AuthToken);
        Assert.Equal("legacy-key", store.Data.DerivedKeyBase64);
        Assert.Equal("legacy-pw", store.Data.SavedPassword);

        // 迁移后立即落盘为 DPAPI 密文
        var content = File.ReadAllText(path);
        Assert.Contains("dpapi:", content);
        Assert.DoesNotContain("legacy-token", content);
    }

    [Fact]
    public void Load_CorruptSecretClearsFieldAndFillsLoadError()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "server_url": "https://srv",
              "auth_token": "dpapi:not-valid-base64!!!"
            }
            """);
        var store = SettingsStore.LoadFromPath(path);
        Assert.NotNull(store.LoadError);
        Assert.Empty(store.Data.AuthToken);
    }
}
