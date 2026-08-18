using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace TextCascadeSharp.Core;

// textcascade.v1 WebSocket 客户端。
// 职责：以 Bearer token + 子协议建立连接、收发 JSON 文本消息、
// 接收看门狗（heartbeatTimeoutSeconds + 10s 无任何字节则 Abort）。
// 消息语义由 ISyncListener（TextSyncEngine）处理。
public sealed class SyncClient : IAsyncDisposable
{
    // 单次 ReceiveAsync 的缓冲区
    private const int ReceiveChunkBytes = 16 * 1024;
    // message 缓冲缩容阈值：避免一次性收到大消息后 MemoryStream 长期占用大容量
    private const int MaxRetainedMessageBytes = 64 * 1024;
    // 接收缓冲上限：内容上限默认 512KB，经 Base64 + JSON 包装后约 700KB；4MB 提供约 5 倍余量
    internal static int MaxWebSocketMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    // 握手应用层超时，防止 DNS/网络挂起让重连任务无限卡住
    internal static TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);

    private readonly ClipConfig _config;
    private readonly string _token;
    private readonly ISyncListener _listener;
    private readonly Func<IWebSocketTransport> _transportFactory;
    private readonly TimeProvider _timeProvider;
    // 串行化所有 socket.SendAsync 调用。ClientWebSocket 不支持并发 Send
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private IWebSocketTransport? _socket;
    // 链接到外部 cancellationToken，用于取消接收循环
    private CancellationTokenSource? _cts;
    // 看门狗收包时间戳
    private long _lastRxTimestamp;
    private ITimer? _watchdog;
    private Task _receiveTask = Task.CompletedTask;

    internal SyncClient(
        ClipConfig config,
        string token,
        ISyncListener listener,
        Func<IWebSocketTransport>? transportFactory = null,
        TimeProvider? timeProvider = null)
    {
        _config = config;
        _token = token;
        _listener = listener;
        _transportFactory = transportFactory ?? (() => new ClientWebSocketTransport());
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // 看门狗阈值：heartbeatTimeoutSeconds + 10s 未收到任何字节（含 ping）则中断。
    // 覆盖 server_busy 无声断开场景。
    internal TimeSpan WatchdogTimeout => TimeSpan.FromSeconds(_config.HeartbeatTimeoutSeconds + 10);

    // 建立 WebSocket 连接（Bearer + textcascade.v1 子协议）并启动接收循环。
    // hello 由上层（引擎）在建连后立即发送。
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var socket = _transportFactory();
        _socket = socket;
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        handshakeCts.CancelAfter(HandshakeTimeout);
        try
        {
            await socket.ConnectAsync(
                new Uri(_config.WebsocketUrl),
                _token,
                ClipConfig.SubProtocol,
                _config.TrustAllCertificates,
                handshakeCts.Token).ConfigureAwait(false);
        }
        catch (WebSocketException) when (socket.LastHttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // token 失效：交给上层重新登录
            throw new SessionExpiredException(socket.LastHttpStatusCode.Value);
        }
        catch (WebSocketException) when (socket.LastHttpStatusCode is HttpStatusCode.BadRequest)
        {
            // 子协议协商失败视为致命错误，重连无意义
            throw new FatalProtocolException(UiText.SubprotocolRejected);
        }

        // 接收看门狗：任何字节（含 ping）都会刷新 _lastRxTimestamp
        _lastRxTimestamp = _timeProvider.GetTimestamp();
        _watchdog?.Dispose();
        _watchdog = _timeProvider.CreateTimer(WatchdogTick, null, WatchdogInterval, WatchdogInterval);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public Task SendHelloAsync(HelloMessage hello, CancellationToken cancellationToken)
    {
        return SendJsonAsync(JsonUtil.Hello(hello), cancellationToken);
    }

    public Task SendClipAsync(OutboundClipMessage clip, CancellationToken cancellationToken)
    {
        return SendJsonAsync(JsonUtil.Clip(clip), cancellationToken);
    }

    public Task SendPongAsync(PongMessage pong, CancellationToken cancellationToken)
    {
        return SendJsonAsync(JsonUtil.Pong(pong), cancellationToken);
    }

    // 关闭连接：以 close code 1000 正常关闭，超时后强制 Abort
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

    // 序列化并通过 WebSocket 发送。通过 _sendLock 保证同一时刻只有一个 Send 在执行
    private async Task SendJsonAsync(string json, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not open.");
        }

        var bytes = Encoding.UTF8.GetBytes(json);
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

    // 接收循环：持续读取完整 WebSocket 文本消息并按 type 分发给 listener。
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
                        // ValueWebSocketReceiveResult 不携带 close code，从 socket 读取
                        var closeStatus = socket.CloseStatus;
                        var reason = closeStatus switch
                        {
                            WebSocketCloseStatus.EndpointUnavailable => "server going away",
                            WebSocketCloseStatus.PolicyViolation => "policy violation",
                            WebSocketCloseStatus.MessageTooBig => "message too big",
                            _ => "remote close"
                        };
                        await _listener.OnClosedAsync(reason, closeStatus).ConfigureAwait(false);
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
                await DispatchAsync(text).ConfigureAwait(false);
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
            await _listener.OnTransportErrorAsync(error).ConfigureAwait(false);
        }
        finally
        {
            // 如果循环退出且不是因为外部取消或已通知，则视为意外断开
            if (!cancellationToken.IsCancellationRequested && !listenerNotified)
            {
                await _listener.OnClosedAsync("socket closed", null).ConfigureAwait(false);
            }
        }
    }

    // 处理一个完整的文本消息：按 type 字段分发。畸形 JSON / 未知 type 只记录日志，
    // 不拖垮整条连接（服务端对非法上行会回 error，由引擎按错误码处理）
    private async Task DispatchAsync(string text)
    {
        var type = JsonUtil.MessageTypeOf(text);
        try
        {
            switch (type)
            {
                case "welcome":
                    await _listener.OnWelcomeAsync(JsonUtil.ParseWelcome(text)).ConfigureAwait(false);
                    break;
                case "clip":
                    await _listener.OnClipAsync(JsonUtil.ParseClip(text)).ConfigureAwait(false);
                    break;
                case "clip_ack":
                    await _listener.OnClipAckAsync(JsonUtil.ParseClipAck(text)).ConfigureAwait(false);
                    break;
                case "ping":
                    await _listener.OnPingAsync(JsonUtil.ParsePing(text)).ConfigureAwait(false);
                    break;
                case "bye":
                    await _listener.OnByeAsync(JsonUtil.ParseBye(text)).ConfigureAwait(false);
                    break;
                case "error":
                    await _listener.OnErrorFrameAsync(JsonUtil.ParseError(text)).ConfigureAwait(false);
                    break;
                case null:
                    Logger.LogError($"Skipping malformed message (not valid JSON or missing type): {Preview(text)}");
                    break;
                default:
                    Logger.LogError($"Skipping message with unknown type '{type}': {Preview(text)}");
                    break;
            }
        }
        catch (Exception error) when (error is System.Text.Json.JsonException)
        {
            // 契约字段缺失/类型不符：跳过该消息，连接保持
            Logger.LogError($"Skipping message with invalid fields (type={type}): {Preview(text)}", error);
        }
    }

    private static string Preview(string text)
    {
        return text.Length <= 100 ? text : text[..100] + "...";
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

    // 看门狗：超过阈值未收到任何字节则 Abort，由接收循环走既有错误路径触发重连
    private void WatchdogTick(object? state)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }
        if (_timeProvider.GetElapsedTime(_lastRxTimestamp) > WatchdogTimeout)
        {
            Logger.Log("Receive watchdog tripped; aborting stale connection.");
            socket.Abort();
        }
    }
}

// SyncClient 回调接口。通过它把协议事件回传给上层（TextSyncEngine）
public interface ISyncListener
{
    // welcome 到达（服务端确认 hello 并附带最新值）
    Task OnWelcomeAsync(WelcomeMessage welcome);

    // clip 广播到达
    Task OnClipAsync(InboundClipMessage message);

    // clip 发送被服务端确认
    Task OnClipAckAsync(ClipAckMessage ack);

    // 应用层心跳
    Task OnPingAsync(PingMessage ping);

    // 服务端维护/关闭通告
    Task OnByeAsync(ByeMessage bye);

    // 服务端 error 帧
    Task OnErrorFrameAsync(ErrorMessage error);

    // 连接被远端关闭（reason + close code；1001=服务端维护）
    Task OnClosedAsync(string reason, WebSocketCloseStatus? closeStatus);

    // 传输层错误（网络断开、看门狗 Abort 等）
    Task OnTransportErrorAsync(Exception error);
}
