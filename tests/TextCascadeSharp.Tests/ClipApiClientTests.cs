using System.Net;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

// LoginClient 测试（假 HTTP）：成功 / 401 invalid_credentials / 429 rate_limited /
// 协议版本不兼容 / 临时故障 / 响应缺字段。
public class ClipApiClientTests
{
    [Fact]
    public async Task LoginAsync_Success_ParsesAllFields()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Json(ContractSamples.LoginSuccess));
        var client = new ClipApiClient();

        var result = await client.LoginAsync(
            "https://localhosts:8443/", "alice", "pw", trustAllCertificates: false,
            CancellationToken.None, handler);

        Assert.Equal("https://localhosts:8443", result.NormalizedServerUrl);
        Assert.Equal("tok-123", result.Token);
        // §4.1 示例：无毫秒 Z 格式时间 + 默认参数 5/30/60
        Assert.Equal(new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc), result.ExpiresAtUtc);
        Assert.Equal(1, result.ProtocolVersion);
        Assert.Equal(524288L, result.MaxTextBytes);
        Assert.Equal(5, result.HelloTimeoutSeconds);
        Assert.Equal(30, result.HeartbeatIntervalSeconds);
        Assert.Equal(60, result.HeartbeatTimeoutSeconds);
    }

    [Fact]
    public async Task LoginAsync_PostsJsonLoginRequest()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Json(ContractSamples.LoginSuccess));
        var requests = new List<HttpRequestMessage>();
        var bodies = new List<string>();
        handler.OnRequest = async request =>
        {
            requests.Add(request);
            // 请求内容在 PostAsync 完成后会被 HttpClient 释放，发送期间读取
            bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync());
        };
        var client = new ClipApiClient();

        await client.LoginAsync("https://srv", "alice", "s3cret", true, CancellationToken.None, handler);

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://srv/api/v1/login", request.RequestUri!.ToString());
        Assert.NotNull(request.Content);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("""{"username":"alice","password":"s3cret"}""", Assert.Single(bodies));
    }

    [Fact]
    public async Task LoginAsync_401InvalidCredentials_ThrowsInvalidCredential()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Json(ContractSamples.LoginInvalidCredentials, HttpStatusCode.Unauthorized));
        var client = new ClipApiClient();

        await Assert.ThrowsAsync<InvalidCredentialException>(
            () => client.LoginAsync("https://srv", "alice", "wrong", false, CancellationToken.None, handler));
    }

    [Fact]
    public async Task LoginAsync_429RateLimited_ThrowsRateLimited()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Json(ContractSamples.LoginRateLimited, HttpStatusCode.TooManyRequests));
        var client = new ClipApiClient();

        await Assert.ThrowsAsync<RateLimitedException>(
            () => client.LoginAsync("https://srv", "alice", "pw", false, CancellationToken.None, handler));
    }

    [Fact]
    public async Task LoginAsync_HigherProtocolVersion_ThrowsUnsupported()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Json(
            """{"token":"t","expiresAtUtc":"2026-12-31T23:59:59.000Z","protocolVersion":2}"""));
        var client = new ClipApiClient();

        var error = await Assert.ThrowsAsync<ProtocolVersionNotSupportedException>(
            () => client.LoginAsync("https://srv", "alice", "pw", false, CancellationToken.None, handler));
        Assert.Equal(2, error.ServerVersion);
        Assert.Equal(1, error.SupportedVersion);
    }

    [Fact]
    public async Task LoginAsync_ServerError_ThrowsTransient()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Json("""{"code":"internal"}""", HttpStatusCode.InternalServerError));
        var client = new ClipApiClient();

        var error = await Assert.ThrowsAsync<CoreException>(
            () => client.LoginAsync("https://srv", "alice", "pw", false, CancellationToken.None, handler));
        Assert.Equal(ErrorCodes.LoginRequestFailed, error.ErrorCode);
    }

    [Theory]
    [InlineData("""{"expiresAtUtc":"2026-12-31T23:59:59.000Z","protocolVersion":1}""")] // 缺 token
    [InlineData("""{"token":"t","protocolVersion":1}""")] // 缺 expiresAtUtc
    [InlineData("""{"token":"t","expiresAtUtc":"not-a-date","protocolVersion":1}""")] // 时间无法解析
    public async Task LoginAsync_MissingRequiredFields_Throws(string body)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Json(body));
        var client = new ClipApiClient();

        var error = await Assert.ThrowsAsync<CoreException>(
            () => client.LoginAsync("https://srv", "alice", "pw", false, CancellationToken.None, handler));
        Assert.Equal(ErrorCodes.LoginResponseInvalid, error.ErrorCode);
    }

    [Fact]
    public async Task LoginAsync_MissingOptionalFields_UsesDefaults()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Json(
            """{"token":"t","expiresAtUtc":"2026-12-31T23:59:59.000Z","protocolVersion":1}"""));
        var client = new ClipApiClient();

        var result = await client.LoginAsync("https://srv", "alice", "pw", false, CancellationToken.None, handler);
        Assert.Equal(ClipConfig.DefaultMaxTextBytes, result.MaxTextBytes);
        Assert.Equal(ClipConfig.DefaultHelloTimeoutSeconds, result.HelloTimeoutSeconds);
        Assert.Equal(ClipConfig.DefaultHeartbeatIntervalSeconds, result.HeartbeatIntervalSeconds);
        Assert.Equal(ClipConfig.DefaultHeartbeatTimeoutSeconds, result.HeartbeatTimeoutSeconds);
    }
}
