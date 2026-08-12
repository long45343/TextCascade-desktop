using System.Net;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

public class ClipApiClientTests
{
    private const string LoginPageHtml =
        "<html><body><form><input type=\"hidden\" name=\"_csrf\" value=\"page-csrf\" /></form></body></html>";

    private static void EnqueueHappyPath(FakeHttpMessageHandler handler)
    {
        handler.Enqueue(FakeHttpMessageHandler.Html(LoginPageHtml));
        handler.Enqueue(FakeHttpMessageHandler.LoginSuccess());
        handler.Enqueue(FakeHttpMessageHandler.Json("{\"mode\":\"P2S\"}"));
        handler.Enqueue(FakeHttpMessageHandler.Json("{\"maxsize\":12345}"));
        handler.Enqueue(FakeHttpMessageHandler.Json("{\"token\":\"logout-csrf\"}"));
    }

    [Fact]
    public async Task Login_HappyPath_ReturnsCookieAndMaxSize()
    {
        using var handler = new FakeHttpMessageHandler();
        EnqueueHappyPath(handler);
        var client = new ClipApiClient();

        var result = await client.LoginAsync(
            "http://localhost:8080",
            "alice",
            "sha3hex",
            CancellationToken.None,
            handler);

        Assert.Equal("http://localhost:8080", result.NormalizedServerUrl);
        Assert.Equal("ws://localhost:8080/clipsocket", result.WebsocketUrl);
        Assert.Equal("JSESSIONID=abc123", result.CookieHeader);
        Assert.Equal(12345, result.MaxSizeBytes);
        Assert.Equal("logout-csrf", result.CsrfToken);
        Assert.Equal(5, handler.RequestCount);
    }

    [Fact]
    public async Task Login_CsrfInput_SingleQuotes_Extracted()
    {
        using var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Html(
            "<input type='hidden' name='_csrf' value='single-csrf' />"));
        handler.Enqueue(FakeHttpMessageHandler.LoginSuccess());
        handler.Enqueue(FakeHttpMessageHandler.Json("{\"mode\":\"P2S\"}"));
        handler.Enqueue(FakeHttpMessageHandler.Json("{\"maxsize\":1}"));
        handler.Enqueue(FakeHttpMessageHandler.Json("{\"token\":\"t\"}"));
        var client = new ClipApiClient();

        var result = await client.LoginAsync(
            "http://localhost:8080",
            "alice",
            "sha3hex",
            CancellationToken.None,
            handler);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Login_CsrfInput_AttributesReordered_Extracted()
    {
        using var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Html(
            "<input value=\"reordered\" name=\"_csrf\" type=\"hidden\" />"));
        handler.Enqueue(FakeHttpMessageHandler.LoginSuccess());
        handler.Enqueue(FakeHttpMessageHandler.Json("{\"mode\":\"P2S\"}"));
        handler.Enqueue(FakeHttpMessageHandler.Json("{\"maxsize\":1}"));
        handler.Enqueue(FakeHttpMessageHandler.Json("{\"token\":\"t\"}"));
        var client = new ClipApiClient();

        var result = await client.LoginAsync(
            "http://localhost:8080",
            "alice",
            "sha3hex",
            CancellationToken.None,
            handler);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Login_CsrfMissing_Throws()
    {
        using var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Html("<html>no form</html>"));
        var client = new ClipApiClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.LoginAsync("http://localhost:8080", "alice", "sha3hex", CancellationToken.None, handler));
    }

    [Fact]
    public async Task Login_BadCredentials_Throws()
    {
        using var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Html(LoginPageHtml));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Invalid username or password: bad credentials")
        });
        var client = new ClipApiClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.LoginAsync("http://localhost:8080", "alice", "sha3hex", CancellationToken.None, handler));
    }

    [Fact]
    public async Task Login_NonP2SMode_Throws()
    {
        using var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Html(LoginPageHtml));
        handler.Enqueue(FakeHttpMessageHandler.LoginSuccess());
        handler.Enqueue(FakeHttpMessageHandler.Json("{\"mode\":\"S2S\"}"));
        var client = new ClipApiClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.LoginAsync("http://localhost:8080", "alice", "sha3hex", CancellationToken.None, handler));
    }

    [Fact]
    public async Task Login_MaxSizeMissing_UsesDefault()
    {
        using var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Html(LoginPageHtml));
        handler.Enqueue(FakeHttpMessageHandler.LoginSuccess());
        handler.Enqueue(FakeHttpMessageHandler.Json("{\"mode\":\"P2S\"}"));
        handler.Enqueue(FakeHttpMessageHandler.Json("{}"));
        handler.Enqueue(FakeHttpMessageHandler.Json("{\"token\":\"t\"}"));
        var client = new ClipApiClient();

        var result = await client.LoginAsync(
            "http://localhost:8080",
            "alice",
            "sha3hex",
            CancellationToken.None,
            handler);

        Assert.Equal(ClipConfig.DefaultMaxSizeBytes, result.MaxSizeBytes);
    }

    [Fact]
    public async Task Login_NonJsonConfigEndpoint_Throws()
    {
        using var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Html(LoginPageHtml));
        handler.Enqueue(FakeHttpMessageHandler.LoginSuccess());
        handler.Enqueue(FakeHttpMessageHandler.Html("<html>not json</html>"));
        var client = new ClipApiClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.LoginAsync("http://localhost:8080", "alice", "sha3hex", CancellationToken.None, handler));
    }

    [Fact]
    public async Task Logout_EmptyCookie_NoRequestSent()
    {
        using var handler = new FakeHttpMessageHandler();
        var client = new ClipApiClient();

        await client.LogoutAsync("http://localhost:8080", "", "csrf", CancellationToken.None, handler);

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task BuildCookieHeader_MultipleCookies_Joined()
    {
        var container = new CookieContainer();
        var uri = new Uri("http://localhost:8080");
        container.Add(uri, new Cookie("a", "1"));
        container.Add(uri, new Cookie("b", "2"));

        var header = ClipApiClient.BuildCookieHeader(container, uri);

        Assert.Contains("a=1", header, StringComparison.Ordinal);
        Assert.Contains("b=2", header, StringComparison.Ordinal);
        Assert.True(header.IndexOf("a=1", StringComparison.Ordinal) < header.IndexOf("b=2", StringComparison.Ordinal));
    }
}
