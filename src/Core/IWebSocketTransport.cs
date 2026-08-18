using System.Net.WebSockets;

namespace TextCascadeSharp.Core;

// WebSocket 传输层抽象：让 SyncClient 可注入假实现做协议级测试。
// 生产默认使用 ClientWebSocketTransport。
internal interface IWebSocketTransport : IDisposable
{
    WebSocketState State { get; }

    // 握手失败时的 HTTP 状态码（CollectHttpResponseDetails=true 时有效，否则 null）
    System.Net.HttpStatusCode? LastHttpStatusCode { get; }

    // 收到远端 Close 帧后反映的 close code（ValueWebSocketReceiveResult 不携带）
    WebSocketCloseStatus? CloseStatus { get; }

    Task ConnectAsync(Uri uri, string bearerToken, string subProtocol, bool trustAllCertificates, CancellationToken cancellationToken);

    Task SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType type,
        WebSocketMessageFlags flags,
        CancellationToken cancellationToken);

    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);

    Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken);

    void Abort();
}

// ClientWebSocket 的默认实现。携带 Bearer token 与 textcascade.v1 子协议，
// 收集握手 HTTP 状态码供 401/400 识别使用。
internal sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private readonly ClientWebSocket _socket = new();

    public WebSocketState State => _socket.State;

    public System.Net.HttpStatusCode? LastHttpStatusCode { get; private set; }

    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    public async Task ConnectAsync(Uri uri, string bearerToken, string subProtocol, bool trustAllCertificates, CancellationToken cancellationToken)
    {
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + bearerToken);
        _socket.Options.AddSubProtocol(subProtocol);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        // .NET 9+ 支持在握手失败时读取 HTTP 状态码，用于识别 token 失效/子协议协商失败
        _socket.Options.CollectHttpResponseDetails = true;
        if (trustAllCertificates)
        {
            // 自签部署场景：用户显式选择信任所有证书
            _socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }
        try
        {
            await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            LastHttpStatusCode = _socket.HttpStatusCode;
            throw;
        }
    }

    public Task SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType type,
        WebSocketMessageFlags flags,
        CancellationToken cancellationToken)
    {
        // .NET 10 的该重载返回 ValueTask，统一转成接口约定的 Task
        return _socket.SendAsync(payload, type, flags, cancellationToken).AsTask();
    }

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        return _socket.ReceiveAsync(buffer, cancellationToken);
    }

    public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
    {
        return _socket.CloseAsync(status, description, cancellationToken);
    }

    public void Abort()
    {
        _socket.Abort();
    }

    public void Dispose()
    {
        _socket.Dispose();
    }
}
