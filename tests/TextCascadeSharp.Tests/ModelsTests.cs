using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

public class ModelsTests
{
    [Fact]
    public void WebsocketUrlFromServerUrl_HttpsToWss()
    {
        Assert.Equal("wss://your-server:8443/api/v1/sync", ClipConfig.WebsocketUrlFromServerUrl("https://your-server:8443"));
    }

    [Fact]
    public void WebsocketUrlFromServerUrl_HttpToWs()
    {
        Assert.Equal("ws://localhost:8080/api/v1/sync", ClipConfig.WebsocketUrlFromServerUrl("http://localhost:8080"));
    }

    [Fact]
    public void WebsocketUrlFromServerUrl_ReplacesPathAndTrailingSlash()
    {
        Assert.Equal("wss://example.com/api/v1/sync", ClipConfig.WebsocketUrlFromServerUrl("https://example.com/some/path/"));
        Assert.Equal("wss://example.com:9000/api/v1/sync", ClipConfig.WebsocketUrlFromServerUrl("https://example.com:9000/old"));
    }

    [Fact]
    public void WebsocketUrlFromServerUrl_SchemeCaseInsensitive()
    {
        Assert.Equal("wss://host/api/v1/sync", ClipConfig.WebsocketUrlFromServerUrl("HTTPS://host"));
        Assert.Equal("ws://host/api/v1/sync", ClipConfig.WebsocketUrlFromServerUrl("Http://host"));
    }

    [Theory]
    [InlineData("ftp://host")]
    [InlineData("file:///C:/x")]
    public void WebsocketUrlFromServerUrl_UnsupportedScheme_Throws(string serverUrl)
    {
        var error = Assert.Throws<CoreException>(() => ClipConfig.WebsocketUrlFromServerUrl(serverUrl));
        Assert.Equal(ErrorCodes.UnsupportedServerUrlScheme, error.ErrorCode);
    }

    [Fact]
    public void WebsocketUrlFromServerUrl_InvalidUrl_Throws()
    {
        var error = Assert.Throws<CoreException>(() => ClipConfig.WebsocketUrlFromServerUrl("not a url"));
        Assert.Equal(ErrorCodes.InvalidServerUrl, error.ErrorCode);
    }

    [Fact]
    public void FromSettings_MapsAllFields()
    {
        var data = new SettingsData
        {
            ServerUrl = "https://srv",
            AuthToken = "tok",
            TokenExpiresAtUtc = "2026-12-31T23:59:59.000Z",
            Username = "alice",
            ClientId = "uuid-1",
            ClientName = "PC",
            LastServerVersion = 42UL,
            MaxTextBytes = 1234L,
            HelloTimeoutSeconds = 11,
            HeartbeatIntervalSeconds = 22,
            HeartbeatTimeoutSeconds = 33,
            HashRounds = 777,
            Salt = "s",
            DerivedKeyBase64 = "key",
            CipherEnabled = true,
            TrustAllCertificates = true,
            ServerCertificateThumbprint = "AA:BB:CC",
            RelaunchOnBoot = true,
            WebsocketStatusNotification = true,
            LocalMaxClipboardBytes = 999L
        };
        var store = new SettingsStore("unused.json", data);
        var config = ClipConfig.FromSettings(store);

        Assert.Equal("https://srv", config.ServerUrl);
        Assert.Equal("tok", config.AuthToken);
        Assert.Equal(new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc), config.TokenExpiresAtUtc);
        Assert.Equal("alice", config.Username);
        Assert.Equal("uuid-1", config.ClientId);
        Assert.Equal("PC", config.ClientName);
        Assert.Equal(42UL, config.LastServerVersion);
        Assert.Equal(1234L, config.MaxTextBytes);
        Assert.Equal(11, config.HelloTimeoutSeconds);
        Assert.Equal(22, config.HeartbeatIntervalSeconds);
        Assert.Equal(33, config.HeartbeatTimeoutSeconds);
        Assert.Equal(777, config.HashRounds);
        Assert.Equal("s", config.Salt);
        Assert.Equal("key", config.DerivedKeyBase64);
        Assert.True(config.CipherEnabled);
        Assert.True(config.TrustAllCertificates);
        Assert.Equal("AA:BB:CC", config.ServerCertificateThumbprint);
        Assert.True(config.RelaunchOnBoot);
        Assert.True(config.WebsocketStatusNotification);
        Assert.Equal(999L, config.LocalMaxClipboardBytes);
        // WebSocket URL 由 ServerUrl 派生
        Assert.Equal("wss://srv/api/v1/sync", config.WebsocketUrl);
    }

    [Fact]
    public void FromSettings_EmptyTokenExpiry_MapsToNull()
    {
        var store = new SettingsStore("unused.json", new SettingsData());
        Assert.Null(ClipConfig.FromSettings(store).TokenExpiresAtUtc);
    }

    [Fact]
    public void Constants_MatchContract()
    {
        Assert.Equal(1, ClipConfig.SupportedProtocolVersion);
        Assert.Equal("textcascade.v1", ClipConfig.SubProtocol);
        Assert.Equal(664_937, ClipConfig.DefaultHashRounds);
        Assert.Equal(512_000L, ClipConfig.DefaultMaxTextBytes);
    }
}

