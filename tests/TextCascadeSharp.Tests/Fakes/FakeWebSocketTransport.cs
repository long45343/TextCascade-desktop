using System.Net.WebSockets;
using System.Text;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.Tests.Fakes;

// 供 SyncClient/TextSyncEngine 测试使用的内存 WebSocket 传输：
//   - Queue 驱动 ReceiveAsync，信号量唤醒等待中的接收循环
//   - 记录 Sent/Abort/Close/Connect/Dispose 次数与握手参数
//   - 可配置握手状态码、发送失败和握手挂起
internal sealed class FakeWebSocketTransport : IWebSocketTransport
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly List<byte[]> _sent = new();
    private readonly System.Net.HttpStatusCode? _handshakeStatus;
    private int _disposedCount;

    public FakeWebSocketTransport(System.Net.HttpStatusCode? handshakeStatus = null)
    {
        _handshakeStatus = handshakeStatus;
    }

    public WebSocketState State { get; private set; }

    public System.Net.HttpStatusCode? LastHttpStatusCode { get; private set; }

    // 模拟远端 Close 帧携带的 close code
    public WebSocketCloseStatus? CloseStatus { get; set; }

    // 最近一次 ConnectAsync 收到的参数
    public Uri? LastConnectUri { get; private set; }

    public string? LastBearerToken { get; private set; }

    public string? LastSubProtocol { get; private set; }

    public bool LastTrustAllCertificates { get; private set; }

    public string? LastServerCertificateThumbprint { get; private set; }

    public bool BlockConnect { get; set; }

    public bool FailSends { get; set; }

    public IReadOnlyList<byte[]> Sent
    {
        get
        {
            lock (_gate)
            {
                return _sent.ToArray();
            }
        }
    }

    public int ConnectCallCount { get; private set; }

    public int AbortCount { get; private set; }

    public int CloseCount { get; private set; }

    public int DisposedCount
    {
        get
        {
            lock (_gate)
            {
                return _disposedCount;
            }
        }
    }

    public int SendCallCount { get; private set; }

    public void Enqueue(string text) => Enqueue(Encoding.UTF8.GetBytes(text));

    public void Enqueue(byte[] bytes)
    {
        lock (_gate)
        {
            _pending.Enqueue(bytes);
            _signal.Release();
        }
    }

    // 注入一个远端 Close 帧（MessageType=Close + 可选 close code）
    public void EnqueueClose(WebSocketCloseStatus? closeStatus = null)
    {
        CloseStatus = closeStatus;
        _closeQueued = true;
        Enqueue([]);
    }

    private bool _closeQueued;

    // 便捷方法：读取已发送文本消息列表
    public IReadOnlyList<string> SentTexts()
    {
        lock (_gate)
        {
            return _sent.Select(static bytes => Encoding.UTF8.GetString(bytes)).ToArray();
        }
    }

    public async Task ConnectAsync(Uri uri, string bearerToken, string subProtocol, bool trustAllCertificates, string serverCertificateThumbprint, CancellationToken cancellationToken)
    {
        ConnectCallCount++;
        LastConnectUri = uri;
        LastBearerToken = bearerToken;
        LastSubProtocol = subProtocol;
        LastTrustAllCertificates = trustAllCertificates;
        LastServerCertificateThumbprint = serverCertificateThumbprint;
        if (_handshakeStatus is { } status)
        {
            LastHttpStatusCode = status;
            throw new WebSocketException($"Handshake rejected (HTTP {(int)status}).");
        }
        if (BlockConnect)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        State = WebSocketState.Open;
    }

    public Task SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType type,
        WebSocketMessageFlags flags,
        CancellationToken cancellationToken)
    {
        SendCallCount++;
        if (FailSends)
        {
            throw new WebSocketException("Simulated send failure.");
        }
        lock (_gate)
        {
            _sent.Add(payload.ToArray());
        }
        return Task.CompletedTask;
    }

    public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            byte[]? item = null;
            lock (_gate)
            {
                if (_pending.Count > 0)
                {
                    item = _pending.Peek();
                }
            }
            if (item is null)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    continue;
                }
                item = _pending.Dequeue();
                // 空载荷 + close 标记表示远端 Close 帧
                if (item.Length == 0 && _closeQueued)
                {
                    _closeQueued = false;
                    return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
                }
                var count = Math.Min(item.Length, buffer.Length);
                item.AsSpan(0, count).CopyTo(buffer.Span);
                if (count < item.Length)
                {
                    // 超出缓冲的部分回到队列，标记为非完整消息
                    _pending.Enqueue(item[count..]);
                    return new ValueWebSocketReceiveResult(count, WebSocketMessageType.Text, endOfMessage: false);
                }
                return new ValueWebSocketReceiveResult(count, WebSocketMessageType.Text, endOfMessage: true);
            }
        }
    }

    public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
    {
        CloseCount++;
        State = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public void Abort()
    {
        AbortCount++;
        State = WebSocketState.Aborted;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposedCount++;
        }
    }
}

