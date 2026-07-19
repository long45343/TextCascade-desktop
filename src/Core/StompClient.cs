using System.Net.WebSockets;
using System.Text;

namespace TextCascadeSharp.Core;

// STOMP 1.1 over WebSocket 客户端。
// 协议参考：https://stomp.github.io/stomp-specification-1.1.html
// 关键点：
//   - 帧以 NULL 字符 (\0) 结尾
//   - 心跳为单个 \n 字节
//   - SEND 帧的 body 不限制格式，但约定为 JSON
public sealed class StompClient : IAsyncDisposable
{
    // 单次 ReceiveAsync 的缓冲区。STOMP 帧可能跨多次接收
    private const int ReceiveChunkBytes = 16 * 1024;
    // _receiveBuffer 缩容阈值：避免长连接空闲后仍占用大块内存
    private const int MaxRetainedReceiveChars = 64 * 1024;
    // message 缓冲缩容阈值：避免一次性收到大消息后 MemoryStream 长期占用大容量
    private const int MaxRetainedMessageBytes = 64 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    // STOMP 心跳帧内容：单个 \n。预编码为字节数组避免每次发送都重新分配
    private static readonly byte[] HeartbeatBytes = "\n"u8.ToArray();

    private readonly string _websocketUrl;
    private readonly string _cookieHeader;
    private readonly IStompListener _listener;
    // 入站字节流可能包含多个不完整帧，需要累加直到遇到 \0 才能解析
    private readonly StringBuilder _receiveBuffer = new();
    // 串行化所有 socket.SendAsync 调用。ClientWebSocket 不支持并发 Send
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    // 订阅 ID 自增计数器，保证每个 SUBSCRIBE 帧的 id 唯一
    private int _subscriptionCounter;
    // 链接到外部 cancellationToken，用于取消接收循环
    private CancellationTokenSource? _cts;

    public StompClient(string websocketUrl, string cookieHeader, IStompListener listener)
    {
        _websocketUrl = websocketUrl;
        _cookieHeader = cookieHeader;
        _listener = listener;
    }

    // 建立 WebSocket 连接并发送 STOMP CONNECT 帧。
    // heart-beat=0,20000 表示：本端不发心跳，但能接受服务端 20 秒间隔的心跳。
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(_cookieHeader))
        {
            // Spring Security 要求 WebSocket 握手时携带 JSESSIONID cookie
            socket.Options.SetRequestHeader("Cookie", _cookieHeader);
        }
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket = socket;

        await socket.ConnectAsync(new Uri(_websocketUrl), cancellationToken).ConfigureAwait(false);
        await SendFrameAsync("CONNECT", new Dictionary<string, string>
        {
            ["host"] = _websocketUrl,
            ["accept-version"] = "1.0,1.1",
            ["heart-beat"] = "0,20000"
        }, string.Empty, cancellationToken).ConfigureAwait(false);
        // 接收循环放在后台线程，不阻塞 ConnectAsync
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    // 订阅指定 destination。服务端会向该 sub-id 推送 MESSAGE 帧
    public Task SubscribeAsync(string destination, CancellationToken cancellationToken)
    {
        return SendFrameAsync("SUBSCRIBE", new Dictionary<string, string>
        {
            ["id"] = "sub-" + Interlocked.Increment(ref _subscriptionCounter),
            ["destination"] = destination
        }, string.Empty, cancellationToken);
    }

    // 向指定 destination 发送消息。ClipCascade 服务端约定 destination=/app/cliptext
    public Task SendAsync(string destination, string body, CancellationToken cancellationToken)
    {
        return SendFrameAsync("SEND", new Dictionary<string, string>
        {
            ["destination"] = destination
        }, body, cancellationToken);
    }

    // 关闭连接：先尝试优雅关闭（发送 Close 帧），超时后强制 Abort
    public async Task CloseAsync()
    {
        _cts?.Cancel();
        var socket = _socket;
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                using var closeCts = new CancellationTokenSource(CloseTimeout);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", closeCts.Token).ConfigureAwait(false);
            }
            catch
            {
                socket.Abort();
            }
        }
        socket?.Dispose();
        _socket = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        _cts?.Dispose();
    }

    // 序列化一个 STOMP 帧并通过 WebSocket 发送。
    // 通过 _sendLock 保证同一时刻只有一个 SendAsync 在执行
    private async Task SendFrameAsync(
        string command,
        Dictionary<string, string> headers,
        string body,
        CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        var text = new StompFrame(command, headers, body).Marshall();
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, WebSocketMessageFlags.EndOfMessage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // 接收循环：持续读取 WebSocket 数据，组装 STOMP 帧，分发给 listener。
    // 一个 WebSocket 消息可能包含多个 STOMP 帧（多个 \0），
    // 一个 STOMP 帧也可能跨多个 WebSocket 消息（前半段 + 后半段）。
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        var buffer = new byte[ReceiveChunkBytes];
        using var message = new MemoryStream(ReceiveChunkBytes);
        var listenerNotified = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        listenerNotified = true;
                        await _listener.OnClosedAsync("remote close").ConfigureAwait(false);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                await HandleTextAsync(text).ConfigureAwait(false);
                ResetMessageBuffer(message);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭路径，不需要通知 listener
        }
        catch (Exception error)
        {
            listenerNotified = true;
            await _listener.OnErrorAsync(error).ConfigureAwait(false);
        }
        finally
        {
            // 如果循环退出且不是因为外部取消或已通知，则视为意外断开
            if (!cancellationToken.IsCancellationRequested && !listenerNotified)
            {
                await _listener.OnClosedAsync("socket closed").ConfigureAwait(false);
            }
        }
    }

    // 处理一个完整的 WebSocket 文本消息：可能是心跳（仅 \n）或一/多个 STOMP 帧
    private async Task HandleTextAsync(string text)
    {
        // 心跳帧：仅包含 \n 或 \r\n
        if (!string.IsNullOrEmpty(text) && text.All(static c => c is '\n' or '\r'))
        {
            await SendHeartbeatAsync().ConfigureAwait(false);
            return;
        }

        // STOMP 帧以 \0 分隔，逐个解析
        List<StompFrame> frames = [];
        lock (_receiveBuffer)
        {
            _receiveBuffer.Append(text);
            while (true)
            {
                var end = FindFrameTerminator(_receiveBuffer);
                if (end < 0)
                {
                    break;
                }
                var rawFrame = _receiveBuffer.ToString(0, end);
                _receiveBuffer.Remove(0, end + 1);
                if (!string.IsNullOrWhiteSpace(rawFrame))
                {
                    frames.Add(StompFrame.Parse(rawFrame));
                }
            }
        }
        TrimReceiveBuffer();

        foreach (var frame in frames)
        {
            switch (frame.Command)
            {
                case "CONNECTED":
                    // CONNECT 帧的服务端应答，表示握手成功
                    await _listener.OnConnectedAsync().ConfigureAwait(false);
                    break;
                case "MESSAGE":
                    // 服务端推送的剪贴板消息
                    await _listener.OnMessageAsync(frame.Body).ConfigureAwait(false);
                    break;
                case "ERROR":
                    // 服务端报告的错误
                    await _listener.OnErrorAsync(new InvalidOperationException(string.IsNullOrWhiteSpace(frame.Body) ? "STOMP error." : frame.Body)).ConfigureAwait(false);
                    break;
            }
        }
    }

    // 在缓冲区中查找帧结束符 \0 的位置
    private static int FindFrameTerminator(StringBuilder builder)
    {
        for (var index = 0; index < builder.Length; index++)
        {
            if (builder[index] == '\0')
            {
                return index;
            }
        }
        return -1;
    }

    // _receiveBuffer 清空后若容量过大则缩容，避免长连接空闲期占用大块内存
    private void TrimReceiveBuffer()
    {
        if (_receiveBuffer.Length == 0 && _receiveBuffer.Capacity > MaxRetainedReceiveChars)
        {
            _receiveBuffer.Capacity = MaxRetainedReceiveChars;
        }
    }

    // MemoryStream 处理完一个大消息后缩容
    private static void ResetMessageBuffer(MemoryStream message)
    {
        message.SetLength(0);
        if (message.Capacity > MaxRetainedMessageBytes)
        {
            message.Capacity = MaxRetainedMessageBytes;
        }
    }

    // STOMP 心跳响应：收到服务端心跳后回送一个 \n。
    // 必须通过 _sendLock 串行化，否则与 SendFrameAsync 并发会触发
    // ClientWebSocket 的 InvalidOperationException（review issue #6）
    private async Task SendHeartbeatAsync()
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open || _cts?.IsCancellationRequested == true)
        {
            return;
        }

        await _sendLock.WaitAsync(_cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }
            await socket.SendAsync(HeartbeatBytes, WebSocketMessageType.Text, WebSocketMessageFlags.EndOfMessage, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (InvalidOperationException)
        {
            // 等待锁期间 socket 已关闭，忽略
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

// STOMP 客户端回调接口。StompClient 通过它把事件回传给上层（TextSyncEngine）
public interface IStompListener
{
    Task OnConnectedAsync();

    Task OnMessageAsync(string body);

    Task OnClosedAsync(string reason);

    Task OnErrorAsync(Exception error);
}
