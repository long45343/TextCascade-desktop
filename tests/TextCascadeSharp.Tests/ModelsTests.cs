using System;
using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

/// <summary>
/// Models 工具方法测试。
/// 重点覆盖 ClipConfig.WebsocketUrlFromServerUrl 的 URL 转换逻辑
/// （review issue #19 修复：用 Uri.TryCreate 替代直接 Uri 构造）。
/// </summary>
public class ModelsTests
{
    [Theory]
    [InlineData("http://localhost:8080", "ws://localhost:8080/clipsocket")]
    [InlineData("http://localhost:8080/", "ws://localhost:8080/clipsocket")]
    [InlineData("http://localhost:8080//", "ws://localhost:8080/clipsocket")]
    [InlineData("https://example.com", "wss://example.com/clipsocket")]
    [InlineData("https://example.com/", "wss://example.com/clipsocket")]
    [InlineData("http://192.168.1.1:9000", "ws://192.168.1.1:9000/clipsocket")]
    [InlineData("http://example.com/some/path", "ws://example.com/some/path/clipsocket")]
    [InlineData("http://example.com/some/path/", "ws://example.com/some/path/clipsocket")]
    public void WebsocketUrlFromServerUrl_ConvertsCorrectly(string input, string expected)
    {
        var result = ClipConfig.WebsocketUrlFromServerUrl(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("HTTP://localhost:8080", "ws://localhost:8080/clipsocket")]
    [InlineData("HTTPS://localhost:8080", "wss://localhost:8080/clipsocket")]
    [InlineData("Http://LocalHost:8080", "ws://localhost:8080/clipsocket")]
    public void WebsocketUrlFromServerUrl_CaseInsensitive(string input, string expected)
    {
        var result = ClipConfig.WebsocketUrlFromServerUrl(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("ftp://localhost:8080")]
    [InlineData("file:///C:/path")]
    [InlineData("ws://localhost:8080")]
    [InlineData("wss://localhost:8080")]
    public void WebsocketUrlFromServerUrl_UnsupportedScheme_Throws(string input)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ClipConfig.WebsocketUrlFromServerUrl(input));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("http://")]
    [InlineData("://missing-scheme")]
    public void WebsocketUrlFromServerUrl_InvalidUrl_Throws(string input)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ClipConfig.WebsocketUrlFromServerUrl(input));
    }

    [Fact]
    public void ClipConfig_FromSettings_PreservesAllFields()
    {
        var data = new SettingsData
        {
            ServerUrl = "http://x",
            WebsocketUrl = "ws://x/ws",
            Username = "u",
            PasswordSha3 = "sha3",
            HashedPasswordBase64 = "key==",
            CsrfToken = "csrf",
            CookieHeader = "cookie",
            MaxSizeBytes = 100,
            HashRounds = 200,
            Salt = "salt",
            CipherEnabled = false,
            RelaunchOnBoot = true,
            WebsocketStatusNotification = true,
            LocalMaxClipboardBytes = 300
        };
        var store = new SettingsStore("dummy", data);

        var config = ClipConfig.FromSettings(store);

        Assert.Equal(data.ServerUrl, config.ServerUrl);
        Assert.Equal(data.WebsocketUrl, config.WebsocketUrl);
        Assert.Equal(data.Username, config.Username);
        Assert.Equal(data.PasswordSha3, config.PasswordSha3);
        Assert.Equal(data.HashedPasswordBase64, config.HashedPasswordBase64);
        Assert.Equal(data.CsrfToken, config.CsrfToken);
        Assert.Equal(data.CookieHeader, config.CookieHeader);
        Assert.Equal(data.MaxSizeBytes, config.MaxSizeBytes);
        Assert.Equal(data.HashRounds, config.HashRounds);
        Assert.Equal(data.Salt, config.Salt);
        Assert.Equal(data.CipherEnabled, config.CipherEnabled);
        Assert.Equal(data.RelaunchOnBoot, config.RelaunchOnBoot);
        Assert.Equal(data.WebsocketStatusNotification, config.WebsocketStatusNotification);
        Assert.Equal(data.LocalMaxClipboardBytes, config.LocalMaxClipboardBytes);
    }

    [Fact]
    public void ClipConfig_Defaults_AreStable()
    {
        // PBKDF2 默认轮数与各 ClipCascade 客户端约定一致，不能随意修改
        Assert.Equal(664937, ClipConfig.DefaultHashRounds);
        Assert.Equal(512_000L, ClipConfig.DefaultMaxSizeBytes);
    }
}
