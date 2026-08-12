using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private readonly Func<Task>? _onSessionExpired;
    private readonly Action<bool>? _onConnectionChanged;
    private readonly Func<IWebSocketTransport> _transportFactory;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cts = new();
    // 保护 _stopped/_connected/_previousHash/_suppressNextLocal/_firstDisconnectTicks
    private readonly object _stateLock = new();
    // 串行化 Start() 与重连任务的连接建立，避免双会话并发
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private StompClient? _stompClient;
    private bool _stopped = true;
    private bool _connected;
    // 首次断开时间戳，用于退避策略
    private long _firstDisconnectTicks;
    // 最近一次同步内容的 hash
    private ulong? _previousHash;
    // 远端写入本地剪贴板后置 true，跳过下一次本地通知
    private bool _suppressNextLocal;
    // 0=无重连在途，1=有（Interlocked 单飞）
    private int _reconnectInFlight;

    // 测试接缝：覆盖指数退避；生产路径为 null 使用默认策略
    internal TimeSpan? ReconnectDelayOverride { get; set; }

    // 测试接缝：替换 Clipboard.SetText 写实现；生产路径为 null
    internal Func<string, CancellationToken, Task>? ClipboardSetAsync { get; set; }

    internal TextSyncEngine(
        ClipConfig config,
        SynchronizationContext uiContext,
        Action<string> onStatus,
        Action<string> onRemoteTextApplied,
        Func<Task>? onSessionExpired = null,
        Action<bool>? onConnectionChanged = null,
        Func<IWebSocketTransport>? transportFactory = null,
        TimeProvider? timeProvider = null)
    {
        _config = config;
        _uiContext = uiContext;
        _onStatus = onStatus;
        _onRemoteTextApplied = onRemoteTextApplied;
        _onSessionExpired = onSessionExpired;
        _onConnectionChanged = onConnectionChanged;
        _transportFactory = transportFactory ?? (() => new ClientWebSocketTransport());
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        // 让在途重连任务尽快结束；旧任务 finally 也会复位该标志
        Interlocked.Exchange(ref _reconnectInFlight, 0);
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
        _onConnectionChanged?.Invoke(true);
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
    //   4) 写入本地剪贴板（带短退避重试，最终失败走 cmd 兜底）
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

            await InvokeUiAsync(async () =>
            {
                var written = await SetClipboardWithRetryAsync(
                    text,
                    ClipboardSetAsync,
                    cancellationToken: _cts.Token).ConfigureAwait(true);
                if (!written)
                {
                    // 受限环境（如 AppLocker）下 cmd 不可用时会失败，返回 false 由上层报错
                    written = TryClipboardFallback(text);
                }

                if (written)
                {
                    lock (_stateLock)
                    {
                        _previousHash = hash;
                        _suppressNextLocal = true;
                    }
                    _onRemoteTextApplied(text);
                }
                else
                {
                    Status(UiText.ClipboardWriteFailed("Clipboard remains locked."));
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

    // 建立到服务端的 STOMP/WebSocket 连接。
    // 返回值：true=无需由调用方继续重连（成功/关停/会话失效），false=需要继续退避重连。
    private async Task<bool> ConnectAsync()
    {
        if (IsStopped())
        {
            return true;
        }

        // 串行化：Start() 与重连任务并发时，后来者直接返回
        if (!await _connectGate.WaitAsync(0).ConfigureAwait(false))
        {
            return true;
        }

        try
        {
            if (IsStopped())
            {
                return true;
            }

            Status(UiText.Connecting);
            // 关闭并释放可能残留的旧连接
            var oldClient = Interlocked.Exchange(ref _stompClient, null);
            if (oldClient is not null)
            {
                await oldClient.CloseAsync().ConfigureAwait(false);
                await oldClient.DisposeAsync().ConfigureAwait(false);
            }

            var client = new StompClient(
                _config.WebsocketUrl,
                _config.CookieHeader,
                this,
                _transportFactory,
                _timeProvider);
            if (IsStopped())
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return true;
            }

            _stompClient = client;
            await client.ConnectAsync(_cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (SessionExpiredException)
        {
            // 认证失效不重连：交给 App 层决定是否静默重登
            await DisposeClientAsync().ConfigureAwait(false);
            MarkDisconnected();
            Status(UiText.SessionExpiredPleaseLogin);
            if (_onSessionExpired is not null)
            {
                await _onSessionExpired().ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            await DisposeClientAsync().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException error)
        {
            // 握手超时等非关停取消仍按网络错误退避重连
            await DisposeClientAsync().ConfigureAwait(false);
            await OnErrorAsync(error).ConfigureAwait(false);
            return false;
        }
        catch (Exception error)
        {
            await DisposeClientAsync().ConfigureAwait(false);
            await OnErrorAsync(error).ConfigureAwait(false);
            return false;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // 释放当前 StompClient。若 StopAsync 已抢先换走并释放，这里不再触碰。
    private async Task DisposeClientAsync()
    {
        var client = _stompClient;
        if (client is null)
        {
            return;
        }
        var current = Interlocked.CompareExchange(ref _stompClient, null, client);
        if (!ReferenceEquals(current, client))
        {
            return;
        }
        try
        {
            await client.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // 关闭失败不影响后续释放
        }
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 释放失败不影响状态机收敛
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
            // 正常关停：不再抛出，避免火忘任务产生未观察异常
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
        bool wasConnected;
        lock (_stateLock)
        {
            wasConnected = _connected;
            _connected = false;
            if (_firstDisconnectTicks == 0)
            {
                _firstDisconnectTicks = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }
        if (wasConnected)
        {
            _onConnectionChanged?.Invoke(false);
        }
    }

    // 调度下一次重连尝试，带退避；同一时刻最多一个重连任务在途
    private void ScheduleReconnect()
    {
        if (IsStopped())
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _reconnectInFlight, 1, 0) != 0)
        {
            return;
        }

        var delay = ReconnectDelay();
        Status(UiText.Connecting);
        _ = Task.Run(async () =>
        {
            var shouldRetry = false;
            try
            {
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                shouldRetry = await ConnectAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常关停
            }
            catch (ObjectDisposedException)
            {
                // 关停竞态：_cts 已释放（review: W2）
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectInFlight, 0);
            }

            // ConnectAsync 失败路径里 OnErrorAsync 的重连触发被单飞吞掉，
            // 这里在标志复位后补一次，保证退避循环继续；成功连接则不再调度
            if (!shouldRetry)
            {
                ScheduleReconnect();
            }
        });
    }

    // 指数退避策略：断开越久重连间隔越长，避免服务端宕机时被刷屏
    private TimeSpan ReconnectDelay()
    {
        if (ReconnectDelayOverride is { } custom)
        {
            return custom;
        }

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
            try
            {
                return _stopped || _cts.IsCancellationRequested;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
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

    // 异步版 UI 转发：重试间隙让出 UI 线程，消息循环继续泵消息
    private Task InvokeUiAsync(Func<Task> action)
    {
        if (_uiContext == SynchronizationContext.Current)
        {
            return action();
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(static async state =>
        {
            var (work, completion) = ((Func<Task>, TaskCompletionSource))state!;
            try
            {
                await work().ConfigureAwait(true);
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        }, (action, tcs));
        return tcs.Task;
    }

    // 短退避重试：默认 5 次 × 100ms。返回是否成功；最终失败由调用方决定兜底策略
    internal static async Task<bool> SetClipboardWithRetryAsync(
        string text,
        Func<string, CancellationToken, Task>? setAsync = null,
        int maxAttempts = 5,
        TimeSpan? retryDelay = null,
        CancellationToken cancellationToken = default)
    {
        setAsync ??= static (t, _) =>
        {
            Clipboard.SetText(t, TextDataFormat.UnicodeText);
            return Task.CompletedTask;
        };
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(100);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await setAsync(text, cancellationToken).ConfigureAwait(true);
                return true;
            }
            catch (ExternalException) when (attempt < maxAttempts)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
            }
            catch (ExternalException)
            {
                // 最后一轮仍失败：把结果交回调用方决定是否走 cmd 兜底
                return false;
            }
        }
    }

    // cmd 兜底：通过 clip 从标准输入写入文本，比清空剪贴板的 `echo off | clip` 语义更符合预期
    private static bool TryClipboardFallback(string text)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe", "/c clip")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!process.Start())
            {
                return false;
            }
            process.StandardInput.Write(text);
            process.StandardInput.Close();
            if (!process.WaitForExit(1000))
            {
                process.Kill();
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
