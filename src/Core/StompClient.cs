using System.Net.WebSockets;
using System.Text;

namespace TextCascadeSharp.Core;

// STOMP 1.1 over WebSocket 客户端。
// 协议参考：https://stomp.github.io/stomp-specification-1.1.html
// 关键点：
//   - 帧以 NULL 字符 (\0) 结尾
//   - 心跳为单个 \n 字节，且本端按协商只收不回
//   - SEND 帧的 body 不限制格式，但约定为 JSON
public sealed class StompClient : IAsyncDisposable
{
    // 单次 ReceiveAsync 的缓冲区。STOMP 帧可能跨多次接收
    private const int ReceiveChunkBytes = 16 * 1024;
    // _receiveBuffer 缩容阈值：避免长连接空闲后仍占用大块内存
    private const int MaxRetainedReceiveChars = 64 * 1024;
    // message 缓冲缩容阈值：避免一次性收到大消息后 MemoryStream 长期占用大容量
    private const int MaxRetainedMessageBytes = 64 * 1024;
    // 接收缓冲上限：内容上限 512KB，经 Base64 + JSON 包装后约 700KB；4MB 提供约 5 倍余量
    internal static int MaxWebSocketMessageBytes = 4 * 1024 * 1024;
    // 长期无 \0 终止符的帧缓冲视为对端协议违例
    internal static int MaxReceiveBufferChars = 2 * 1024 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    // 握手应用层超时，防止 DNS/网络挂起让重连任务无限卡住
    internal static TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FallbackRxTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MinimumRxTimeout = TimeSpan.FromSeconds(45);
    // 本端 CONNECT 帧宣告的接收能力：能接受服务端 ≤20s 一次的心跳
    private const int ReceiveHeartbeatIntervalMs = 20_000;

    private readonly string _websocketUrl;
    private readonly string _cookieHeader;
    private readonly IStompListener _listener;
    private readonly Func<IWebSocketTransport> _transportFactory;
    private readonly TimeProvider _timeProvider;
    // 入站字节流可能包含多个不完整帧，需要累加直到遇到 \0 才能解析
    private readonly StringBuilder _receiveBuffer = new();
    // 串行化所有 socket.SendAsync 调用。ClientWebSocket 不支持并发 Send
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private IWebSocketTransport? _socket;
    // 订阅 ID 自增计数器，保证每个 SUBSCRIBE 帧的 id 唯一
    private int _subscriptionCounter;
    // 链接到外部 cancellationToken，用于取消接收循环
    private CancellationTokenSource? _cts;
    // 看门狗收包时间戳与阈值
    private long _lastRxTimestamp;
    private TimeSpan _rxTimeout = MinimumRxTimeout;
    private ITimer? _watchdog;
    private TaskCompletionSource _connectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _receiveTask = Task.CompletedTask;

    internal StompClient(
        string websocketUrl,
        string cookieHeader,
        IStompListener listener,
        Func<IWebSocketTransport>? transportFactory = null,
        TimeProvider? timeProvider = null)
    {
        _websocketUrl = websocketUrl;
        _cookieHeader = cookieHeader;
        _listener = listener;
        _transportFactory = transportFactory ?? (() => new ClientWebSocketTransport());
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // 建立 WebSocket 连接并发送 STOMP CONNECT 帧。
    // heart-beat=0,20000 表示：本端不发心跳，但能接受服务端 20 秒间隔的心跳。
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var socket = _transportFactory();
        _socket = socket;
        _connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // HTTP/WebSocket 握手与 STOMP CONNECTED 都必须在该时限内完成，
        // 否则连接会停留在“TCP 已通但协议会话未建立”的半初始化状态。
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        handshakeCts.CancelAfter(HandshakeTimeout);
        try
        {
            await socket.ConnectAsync(new Uri(_websocketUrl), _cookieHeader, handshakeCts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException) when (socket.LastHttpStatusCode is
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new SessionExpiredException(socket.LastHttpStatusCode.Value);
        }

        _lastRxTimestamp = _timeProvider.GetTimestamp();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        try
        {
            await SendFrameAsync("CONNECT", new Dictionary<string, string>
            {
                ["host"] = _websocketUrl,
                ["accept-version"] = "1.0,1.1",
                ["heart-beat"] = "0,20000"
            }, string.Empty, handshakeCts.Token).ConfigureAwait(false);
            await WaitForConnectedDuringHandshakeAsync(handshakeCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // STOMP 半握手不能保留：取消接收循环并等待它收敛后再交给上层清理。
            _cts.Cancel();
            socket.Abort();
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch
            {
                // 接收循环的错误已经通过 listener 上报，这里只等待它退出。
            }
            throw;
        }
    }

    private async Task WaitForConnectedDuringHandshakeAsync(CancellationToken cancellationToken)
    {
        await _connectedTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        var watchdog = Interlocked.Exchange(ref _watchdog, null);
        watchdog?.Dispose();
        var cts = Interlocked.Exchange(ref _cts, null);
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放的 CTS 说明 CloseAsync 被重复调用，忽略即可
        }
        cts?.Dispose();
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket is not null)
        {
            if (socket.State == WebSocketState.Open)
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
            socket.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _sendLock.Dispose();
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
            throw new InvalidOperationException("WebSocket is not open.");
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
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    _lastRxTimestamp = _timeProvider.GetTimestamp();
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        listenerNotified = true;
                        await _listener.OnClosedAsync("remote close").ConfigureAwait(false);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxWebSocketMessageBytes)
                    {
                        throw new InvalidOperationException("WebSocket message exceeded size cap.");
                    }
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
        // 心跳帧：仅刷新收包时间戳，不回送（与 CONNECT 中 heart-beat=0 的协商一致）
        if (!string.IsNullOrEmpty(text) && text.All(static c => c is '\n' or '\r'))
        {
            return;
        }

        // STOMP 帧以 \0 分隔，逐个解析
        List<StompFrame> frames = [];
        lock (_receiveBuffer)
        {
            _receiveBuffer.Append(text);
            if (_receiveBuffer.Length > MaxReceiveBufferChars)
            {
                // 对端长期不发送 \0，按协议违例断开；先清空避免内存继续增长
                _receiveBuffer.Clear();
                throw new InvalidOperationException("STOMP receive buffer exceeded size cap.");
            }
            while (true)
            {
                var end = FindFrameTerminator(_receiveBuffer);
                if (end < 0)
                {
                    break;
                }
                var rawFrame = _receiveBuffer.ToString(0, end);
                _receiveBuffer.Remove(0, end + 1);
                if (string.IsNullOrWhiteSpace(rawFrame))
                {
                    continue;
                }
                try
                {
                    frames.Add(StompFrame.Parse(rawFrame));
                }
                catch (Exception error)
                {
                    // 单个畸形帧只跳过，不拖垮整条连接
                    var preview = rawFrame.Length <= 100 ? rawFrame : rawFrame[..100] + "...";
                    Logger.LogError($"Skipping malformed STOMP frame: {preview}", error);
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
                    _rxTimeout = ComputeServerHeartbeatInterval(frame.Headers) is { } interval
                        ? TimeSpan.FromMilliseconds(Math.Max(2 * interval.TotalMilliseconds, MinimumRxTimeout.TotalMilliseconds))
                        : FallbackRxTimeout;
                    _watchdog?.Dispose();
                    _watchdog = _timeProvider.CreateTimer(WatchdogTick, null, WatchdogInterval, WatchdogInterval);
                    _connectedTcs.TrySetResult();
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

    // 解析 CONNECTED 帧 heart-beat 头第一段（服务端承诺的发送间隔），
    // 与本端宣告的接收能力 20s 取最大值；无承诺时返回 null。
    internal static TimeSpan? ComputeServerHeartbeatInterval(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("heart-beat", out var raw))
        {
            return null;
        }
        var parts = raw.Split(',', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var serverSendInterval) || serverSendInterval <= 0)
        {
            return null;
        }
        return TimeSpan.FromMilliseconds(Math.Max(serverSendInterval, ReceiveHeartbeatIntervalMs));
    }

    // 看门狗：超过阈值未收到任何字节则 Abort，由接收循环走既有错误路径触发重连
    private void WatchdogTick(object? state)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }
        if (_timeProvider.GetElapsedTime(_lastRxTimestamp) > _rxTimeout)
        {
            Logger.Log("Receive watchdog tripped; aborting stale connection.");
            socket.Abort();
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
