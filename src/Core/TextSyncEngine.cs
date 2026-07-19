using System.Text;
using System.Windows.Forms;

namespace TextCascadeSharp.Core;

// 剪贴板同步核心引擎。实现 IStompListener 接收 STOMP 事件，
// 并通过 _uiContext 把需要在 UI 线程执行的操作（写剪贴板）转发回主线程。
//
// 状态机：
//   stopped --Start()--> connecting --OnConnected--> connected
//   任意状态 --OnError/OnClosed--> disconnected --ScheduleReconnect--> connecting
//
// 去重机制：
//   _previousHash 缓存最近一次成功同步的内容 hash，
//   避免本地复制→发送→对端回环→再发送的循环。
//   _suppressNextLocal 用于服务端推送写入本地剪贴板后，
//   跳过因此触发的下一次本地复制通知。
public sealed class TextSyncEngine : IStompListener, IAsyncDisposable
{
    private readonly ClipConfig _config;
    private readonly SynchronizationContext _uiContext;
    private readonly Action<string> _onStatus;
    private readonly Action<string> _onRemoteTextApplied;
    private readonly CancellationTokenSource _cts = new();
    // 保护 _stopped/_connected/_previousHash/_suppressNextLocal/_firstDisconnectTicks
    private readonly object _stateLock = new();
    private StompClient? _stompClient;
    private bool _stopped = true;
    private bool _connected;
    // 首次断开时间戳，用于退避策略
    private long _firstDisconnectTicks;
    // 最近一次同步内容的 hash
    private ulong? _previousHash;
    // 远端写入本地剪贴板后置 true，跳过下一次本地通知
    private bool _suppressNextLocal;

    public TextSyncEngine(
        ClipConfig config,
        SynchronizationContext uiContext,
        Action<string> onStatus,
        Action<string> onRemoteTextApplied)
    {
        _config = config;
        _uiContext = uiContext;
        _onStatus = onStatus;
        _onRemoteTextApplied = onRemoteTextApplied;
    }

    // 启动同步引擎。可重入：若已启动则直接返回。
    public void Start()
    {
        lock (_stateLock)
        {
            if (!_stopped)
            {
                return;
            }
            _stopped = false;
        }
        _ = ConnectAsync();
    }

    // 停止引擎：取消所有异步操作并关闭 STOMP 连接
    public async Task StopAsync()
    {
        lock (_stateLock)
        {
            _stopped = true;
            _connected = false;
        }
        _cts.Cancel();
        var client = Interlocked.Exchange(ref _stompClient, null);
        if (client is not null)
        {
            await client.CloseAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    // 由 ClipboardMonitor 调用，把本地剪贴板新内容广播出去
    public void SendLocalText(string text, string source)
    {
        _ = Task.Run(() => SendLocalTextAsync(text, source, _cts.Token));
    }

    // STOMP CONNECTED 帧到达：握手成功，订阅用户专属队列
    public async Task OnConnectedAsync()
    {
        lock (_stateLock)
        {
            _connected = true;
            _firstDisconnectTicks = 0;
        }
        Status(UiText.Connected);
        var client = _stompClient;
        if (client is not null)
        {
            // /user/queue/cliptext 是 Spring Boot 用户目的地，
            // 服务端会把 /app/cliptext 收到的消息转发到每个用户的这个队列
            await client.SubscribeAsync("/user/queue/cliptext", _cts.Token).ConfigureAwait(false);
        }
    }

    // STOMP MESSAGE 帧到达：远端发来了新剪贴板内容。
    // 处理顺序（关键，见 review issue #4/#5/#8）：
    //   1) 解密
    //   2) 大小检查（失败直接 return，不修改任何状态）
    //   3) hash 去重检查
    //   4) 写入本地剪贴板
    //   5) 写入成功后才更新 _previousHash 和 _suppressNextLocal
    public async Task OnMessageAsync(string body)
    {
        try
        {
            var message = JsonUtil.ParseClipMessage(body);
            if (!message.Type.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var text = message.Payload;
            if (_config.CipherEnabled)
            {
                text = CryptoManager.Decrypt(JsonUtil.ParseEncryptedPayload(text), _config.HashedPasswordBase64);
            }

            if (!IsWithinLimits(text, UiText.DirectionInbound))
            {
                return;
            }

            var hash = HashUtil.Fnv1A64(text);
            lock (_stateLock)
            {
                if (_previousHash == hash)
                {
                    return;
                }
            }

            await InvokeUiAsync(() =>
            {
                try
                {
                    Clipboard.SetText(text, TextDataFormat.UnicodeText);
                    lock (_stateLock)
                    {
                        _previousHash = hash;
                        _suppressNextLocal = true;
                    }
                    _onRemoteTextApplied(text);
                }
                catch (Exception error)
                {
                    Status(UiText.ClipboardWriteFailed(error.Message));
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Status(UiText.InboundError(error.Message));
        }
    }

    public Task OnClosedAsync(string reason)
    {
        MarkDisconnected();
        Status(UiText.Disconnected(reason));
        ScheduleReconnect();
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception error)
    {
        MarkDisconnected();
        Status(UiText.WebSocketError(error.Message));
        ScheduleReconnect();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    // 建立到服务端的 STOMP/WebSocket 连接
    private async Task ConnectAsync()
    {
        if (IsStopped())
        {
            return;
        }

        Status(UiText.Connecting);
        try
        {
            // 关闭并释放可能残留的旧连接
            var oldClient = Interlocked.Exchange(ref _stompClient, null);
            if (oldClient is not null)
            {
                await oldClient.CloseAsync().ConfigureAwait(false);
                await oldClient.DisposeAsync().ConfigureAwait(false);
            }

            var client = new StompClient(_config.WebsocketUrl, _config.CookieHeader, this);
            _stompClient = client;
            await client.ConnectAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常关闭，不重连
        }
        catch (Exception error)
        {
            await OnErrorAsync(error).ConfigureAwait(false);
        }
    }

    // 处理本地剪贴板新内容：
    //   1) 若 _suppressNextLocal 为 true（说明是远端写入触发的本地通知），跳过
    //   2) 检查是否未连接
    //   3) 检查大小
    //   4) hash 去重
    //   5) 加密
    //   6) 发送
    //   7) 仅在发送成功后才更新 _previousHash（review issue #5）
    private async Task SendLocalTextAsync(string text, string source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_suppressNextLocal)
            {
                _suppressNextLocal = false;
                return;
            }
            if (!_connected)
            {
                Status(UiText.IgnoredNotConnected(source));
                return;
            }
        }

        if (!IsWithinLimits(text, UiText.DirectionOutbound))
        {
            return;
        }

        var hash = HashUtil.Fnv1A64(text);
        lock (_stateLock)
        {
            if (_previousHash == hash)
            {
                return;
            }
        }

        var payload = text;
        if (_config.CipherEnabled)
        {
            payload = JsonUtil.EncryptedPayload(CryptoManager.Encrypt(text, _config.HashedPasswordBase64));
        }

        var client = _stompClient;
        if (client is null)
        {
            return;
        }

        try
        {
            await client.SendAsync("/app/cliptext", JsonUtil.ClipMessage(payload, "text"), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // 发送失败：不更新 _previousHash，下次相同内容仍可重试
            Status(UiText.WebSocketError(error.Message));
            return;
        }

        // 发送成功后才提交 hash，避免失败时被静默丢弃
        lock (_stateLock)
        {
            _previousHash = hash;
        }
        Status(UiText.Broadcasting);
    }

    // 检查内容字节数是否在服务端和本地限制内
    private bool IsWithinLimits(string text, string direction)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        var localLimit = _config.LocalMaxClipboardBytes > 0 ? _config.LocalMaxClipboardBytes : _config.MaxSizeBytes;
        var ok = bytes <= _config.MaxSizeBytes && bytes <= localLimit;
        if (!ok)
        {
            Status(UiText.ClipboardTooLarge(direction, bytes));
        }
        return ok;
    }

    // 标记为已断开，并记录首次断开时间（用于退避计算）
    private void MarkDisconnected()
    {
        lock (_stateLock)
        {
            _connected = false;
            if (_firstDisconnectTicks == 0)
            {
                _firstDisconnectTicks = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }
    }

    // 调度下一次重连尝试，带退避
    private void ScheduleReconnect()
    {
        if (IsStopped())
        {
            return;
        }

        var delay = ReconnectDelay();
        Status(UiText.Connecting);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                await ConnectAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    // 指数退避策略：断开越久重连间隔越长，避免服务端宕机时被刷屏
    private TimeSpan ReconnectDelay()
    {
        long firstDisconnect;
        lock (_stateLock)
        {
            firstDisconnect = _firstDisconnectTicks;
        }
        if (firstDisconnect == 0)
        {
            return TimeSpan.FromSeconds(10);
        }

        var elapsed = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - firstDisconnect) / 1000;
        return elapsed switch
        {
            < 600 => TimeSpan.FromSeconds(10),    // 0-10 分钟：10 秒
            < 1800 => TimeSpan.FromSeconds(60),   // 10-30 分钟：60 秒
            < 3600 => TimeSpan.FromSeconds(180),  // 30-60 分钟：3 分钟
            _ => TimeSpan.FromSeconds(300)         // 1 小时以上：5 分钟
        };
    }

    private bool IsStopped()
    {
        lock (_stateLock)
        {
            return _stopped || _cts.IsCancellationRequested;
        }
    }

    // 把状态消息发到 UI 线程显示。若已在 UI 线程则直接调用
    private void Status(string message)
    {
        if (_uiContext == SynchronizationContext.Current)
        {
            _onStatus(message);
            return;
        }
        _uiContext.Post(static state =>
        {
            var (callback, value) = ((Action<string>, string))state!;
            callback(value);
        }, (_onStatus, message));
    }

    // 把需要在 UI 线程执行的操作转发过去，并返回可等待的 Task
    private Task InvokeUiAsync(Action action)
    {
        if (_uiContext == SynchronizationContext.Current)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(static state =>
        {
            var (work, completion) = ((Action, TaskCompletionSource))state!;
            try
            {
                work();
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        }, (action, tcs));
        return tcs.Task;
    }
}
