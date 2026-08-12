using System.Net.WebSockets;
using System.Text;
using TextCascadeSharp.Core;

namespace TextCascadeSharp.Tests.Fakes;

// 供 StompClient 测试使用的内存 WebSocket 传输：
//   - Queue 驱动 ReceiveAsync，信号量唤醒等待中的接收循环
//   - 记录 Sent/Abort/Close/Connect/Dispose 次数
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

    public async Task ConnectAsync(Uri uri, string? cookieHeader, CancellationToken cancellationToken)
    {
        ConnectCallCount++;
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
                var count = Math.Min(item.Length, buffer.Length);
                item.AsSpan(0, count).CopyTo(buffer.Span);
                if (count < item.Length)
                {
                    _pending.Enqueue(item[count..]);
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
