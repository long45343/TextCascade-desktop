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
    // 入站写剪贴板失败后的后台退避重试档位。首轮同步重试（5×100ms）已在 UI 线程内完成，
    // 这里负责「剪贴板长时间被独占」的情况，不再阻塞 UI 线程。
    internal static TimeSpan[] ApplyRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private readonly ClipConfig _config;
    private readonly SynchronizationContext _uiContext;
    private readonly Action<string> _onStatus;
    private readonly Action<string> _onRemoteTextApplied;
    private readonly Func<Task>? _onSessionExpired;
    private readonly Action<bool>? _onConnectionChanged;
    private readonly Func<IWebSocketTransport> _transportFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan>? _getEvasionDelay;
    private readonly CancellationTokenSource _cts = new();
    // 保护 _stopped/_connected/_previousHash/_suppressNextLocal/_firstDisconnectTicks/_inboundGeneration
    private readonly object _stateLock = new();
    // 串行化 Start() 与重连任务的连接建立，避免双会话并发
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private StompClient? _stompClient;
    private bool _stopped = true;
    private bool _connected;
    // 前台规避期间置位：停止外发监听由 App 层负责，这里只负责入站丢弃写入
    private volatile bool _evasionPaused;
    // 首次断开时间戳，用于退避策略
    private long _firstDisconnectTicks;
    // 同一断线周期内已调度的重连次数；前两次仍尝试旧 cookie。
    private int _reconnectAttempts;
    // 最近一次同步内容的 hash
    private ulong? _previousHash;
    // 远端写入本地剪贴板后置 true，跳过下一次本地通知
    private bool _suppressNextLocal;
    // 入站内容代际：远端应用成功或本地发送提交时自增。
    // 入站失败后的后台重试据此判断「期间已有更新的内容生效，旧重试应放弃」。
    private long _inboundGeneration;
    // 0=无重连在途，1=有（Interlocked 单飞）
    private int _reconnectInFlight;
    // 电源/网络恢复的唤醒源；取消它可打断在途退避等待
    private CancellationTokenSource _wakeCts = new();

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
        TimeProvider? timeProvider = null,
        Func<TimeSpan>? getEvasionDelay = null)
    {
        _config = config;
        _uiContext = uiContext;
        _onStatus = onStatus;
        _onRemoteTextApplied = onRemoteTextApplied;
        _onSessionExpired = onSessionExpired;
        _onConnectionChanged = onConnectionChanged;
        _transportFactory = transportFactory ?? (() => new ClientWebSocketTransport());
        _timeProvider = timeProvider ?? TimeProvider.System;
        _getEvasionDelay = getEvasionDelay;
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
            _reconnectAttempts = 0;
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
            _reconnectAttempts = 0;
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

    // 前台规避开关：App 层在检测到目标远程工具激活时置 true。
    // 规避期间入站内容直接丢弃（不写入本地剪贴板），避免与远程工具的剪贴板互相覆盖。
    public void SetEvasionPaused(bool paused) => _evasionPaused = paused;

    // STOMP MESSAGE 帧到达：远端发来了新剪贴板内容。
    // 处理顺序（关键，见 review issue #4/#5/#8）：
    //   1) 前台规避判定（跳过写入）
    //   2) 可选延时（避开与远程工具的剪贴板锁争用）；延时后复查规避状态
    //   3) 解密
    //   4) 大小检查（失败直接 return，不修改任何状态）
    //   5) hash 去重检查
    //   6) 写入本地剪贴板（带短退避重试；最终失败转后台退避重试）
    //   7) 写入成功后才更新 _previousHash、_suppressNextLocal 与入站代际
    public async Task OnMessageAsync(string body)
    {
        try
        {
            if (_evasionPaused)
            {
                // 前台规避中：完全让渡剪贴板控制权，不入站写本地
                return;
            }

            if (_getEvasionDelay?.Invoke() is { } delay && delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // 服务停止时引擎已释放：静默退出，不上报为入站错误
                    return;
                }
                // 延时途中可能切到了前台规避，或已停止服务：此时放弃写入
                if (_evasionPaused || IsStopped())
                {
                    return;
                }
            }

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

            var written = false;
            await InvokeUiAsync(async () =>
            {
                written = await SetClipboardWithRetryAsync(
                    text,
                    ClipboardSetAsync,
                    cancellationToken: _cts.Token).ConfigureAwait(true);

                if (written)
                {
                    lock (_stateLock)
                    {
                        _previousHash = hash;
                        _suppressNextLocal = true;
                        _inboundGeneration++;
                    }
                    _onRemoteTextApplied(text);
                }
                else
                {
                    Status(UiText.ClipboardWriteFailed("Clipboard remains locked."));
                }
            }).ConfigureAwait(false);

            if (!written)
            {
                // 同步重试已耗尽：转后台退避重试，不再占用 UI 线程。
                // 记录当前代际作为基准，代际推进即代表已有更新内容生效。
                long generation;
                lock (_stateLock)
                {
                    generation = _inboundGeneration;
                }
                _ = RetryApplyAsync(text, hash, generation, _cts.Token);
            }
        }
        catch (Exception error)
        {
            Status(UiText.InboundError(error.Message));
        }
    }

    // 入站写入失败后的后台退避重试。
    // 每次重试前比对代际：若期间远端又应用了更新的内容、或本地发送提交了新内容，
    // 说明这份旧内容已被取代，直接放弃，避免回写覆盖用户刚复制的东西。
    private async Task RetryApplyAsync(string text, ulong hash, long baseGeneration, CancellationToken cancellationToken)
    {
        var delays = ApplyRetryDelays;
        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            try
            {
                if (delays[attempt] > TimeSpan.Zero)
                {
                    await Task.Delay(delays[attempt], cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (IsStopped() || _evasionPaused)
            {
                return;
            }

            lock (_stateLock)
            {
                if (_inboundGeneration != baseGeneration)
                {
                    Logger.Log($"RetryApplyAsync: inbound generation advanced to {_inboundGeneration}, abandoning stale retry.");
                    return;
                }
            }

            Logger.Log($"RetryApplyAsync: attempt {attempt + 1}/{delays.Length}.");
            bool written;
            try
            {
                written = await WriteToClipboardAsync(text).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 服务停止/引擎释放：后台任务必须自行吞掉取消，否则成为未观察异常
                return;
            }
            catch (Exception error)
            {
                Logger.LogError($"RetryApplyAsync: attempt {attempt + 1} threw", error);
                continue;
            }
            if (written)
            {
                lock (_stateLock)
                {
                    _previousHash = hash;
                    _suppressNextLocal = true;
                    _inboundGeneration++;
                }
                Logger.Log($"RetryApplyAsync: attempt {attempt + 1} succeeded.");
                _onRemoteTextApplied(text);
                return;
            }

            Logger.Log($"RetryApplyAsync: attempt {attempt + 1} failed.");
        }

        Logger.Log($"RetryApplyAsync: all {delays.Length} retries exhausted.");
        Status(UiText.ClipboardWriteFailed("Clipboard remains locked after background retries."));
    }

    // 在 UI 线程写入剪贴板（后台重试路径专用）
    private Task<bool> WriteToClipboardAsync(string text)
    {
        return InvokeUiAsync(() => SetClipboardWithRetryAsync(
            text,
            ClipboardSetAsync,
            cancellationToken: _cts.Token));
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
        // 只在引擎真正释放时释放当前唤醒源；NotifyWake 交换出去的旧源交由 GC
        _wakeCts.Dispose();
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
            // 本地发送提交了新内容：任何在途的入站后台重试都基于旧内容，应予放弃
            _inboundGeneration++;
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

        var attempt = Interlocked.Increment(ref _reconnectAttempts);
        var delay = ReconnectDelay();
        Status(attempt <= CookieReconnectAttempts
            ? UiText.Connecting
            : UiText.SessionRecovering);
        _ = Task.Run(async () =>
        {
            var shouldRetry = false;
            try
            {
                await WaitForDelayAsync(delay).ConfigureAwait(false);
                if (attempt <= CookieReconnectAttempts)
                {
                    shouldRetry = await ConnectAsync().ConfigureAwait(false);
                }
                else
                {
                    if (_onSessionExpired is not null)
                    {
                        await _onSessionExpired().ConfigureAwait(false);
                    }
                    shouldRetry = false;
                }
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

    private async Task WaitForDelayAsync(TimeSpan delay)
    {
        var wakeCts = _wakeCts;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, wakeCts.Token);
        try
        {
            await Task.Delay(delay, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            // 被唤醒信号打断：稍等 1 秒让网络栈稳定后，由调用方立即重连
            Logger.Log("Wake signal interrupted reconnect backoff; retrying immediately.");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    // 电源恢复 / 网络恢复时由 App 层调用：
    // 若正处于退避等待，立即打断（约 1 秒后重连），不必等当前退避档位到期。
    // 睡眠后断线时长仍按真实时间计，档位可能已升到 3-5 分钟，因此必须能打断等待。
    public void NotifyWake()
    {
        if (IsStopped())
        {
            return;
        }
        var old = Interlocked.Exchange(ref _wakeCts, new CancellationTokenSource());
        // 只取消不释放：并发的 WaitForDelayAsync 可能已捕获旧源并正要读它的 Token，
        // 立刻 Dispose 会让那次读取抛 ObjectDisposedException。无定时器的 CTS
        // 不持有需要确定性释放的资源，留给 GC 即可。
        try
        {
            old.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
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

    private const int CookieReconnectAttempts = 2;

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
        return InvokeUiAsync<object?>(async () =>
        {
            await action().ConfigureAwait(true);
            return null;
        });
    }

    // 带返回值的 UI 转发
    private Task<T> InvokeUiAsync<T>(Func<Task<T>> action)
    {
        if (_uiContext == SynchronizationContext.Current)
        {
            return action();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(static async state =>
        {
            var (work, completion) = ((Func<Task<T>>, TaskCompletionSource<T>))state!;
            try
            {
                completion.SetResult(await work().ConfigureAwait(true));
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        }, (action, tcs));
        return tcs.Task;
    }

    // 短退避重试：默认 5 次 × 100ms。返回是否成功；最终失败由调用方转入后台退避重试。
    // 不再提供 clip.exe 兜底：起外部进程会阻塞 UI 线程（最长 1 秒），且失败后语义与后台重试重复。
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
                // 最后一轮仍失败：交回调用方决定是否转后台重试
                return false;
            }
        }
    }
}
