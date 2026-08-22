using System.Net.WebSockets;

namespace TextCascadeSharp.Core;

// 剪贴板同步核心引擎。实现 ISyncListener 接收 textcascade.v1 协议事件，
// 并通过 _uiContext 把需要在 UI 线程执行的操作（读写剪贴板）转发回主线程。
//
// 状态机：
//   stopped --Start()--> connecting --hello 发出--> connected
//   任意状态 --传输错误/远端关闭--> disconnected --ScheduleReconnect--> connecting
//
// 去重机制（迁至 SyncSession）：
//   版本游标、hash 去重、回环抑制、发送暂停、加解密衔接、大小限制
//
// 退避策略（收到 welcome 后重置）：
//   普通断开：1s、2s、5s、10s、30s、60s，之后固定 60s
//   维护断开（bye/close 1001）：1s、2s、5s、10s，之后固定 10s
public sealed class TextSyncEngine : ISyncListener, IAsyncDisposable
{
    // token 距 expiresAtUtc 不足该余量时视为即将过期，直接走重登
    private static readonly TimeSpan TokenExpiryMargin = TimeSpan.FromSeconds(30);

    private readonly ClipConfig _config;
    private readonly SynchronizationContext _uiContext;
    private readonly Action<string> _onStatus;
    private readonly Action<string> _onRemoteTextApplied;
    private readonly Func<Task>? _onSessionExpired;
    private readonly Action<bool>? _onConnectionChanged;
    private readonly Func<IWebSocketTransport> _transportFactory;
    private readonly TimeProvider _timeProvider;
    // 版本游标推进回调：App 层用它把 lastServerVersion 写回 settings.json
    private readonly Action<ulong>? _onServerVersionAdvanced;
    // 保护 _stopped/_connected/_sawBye
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _cts = new();
    // 串行化 Start() 与重连任务的连接建立，避免双会话并发
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    // 重连调度：退避档位、单飞、唤醒打断
    private readonly ReconnectPolicy _reconnectPolicy;
    // 协议消息领域语义：welcome/clip/clip_ack/error 应用、去重、回环抑制、发送
    private readonly SyncSession _session;
    private SyncClient? _client;
    private bool _stopped = true;
    private bool _connected;
    // 收到 bye 后置位，随后的关闭事件走温和退避
    private bool _sawBye;

    // 剪贴板读写桥（UI 线程 + 重试 + cmd 兜底）。测试可注入 fake 桥
    private readonly ClipboardBridge _clipboard;

    internal TextSyncEngine(
        ClipConfig config,
        SynchronizationContext uiContext,
        Action<string> onStatus,
        Action<string> onRemoteTextApplied,
        Func<Task>? onSessionExpired = null,
        Action<bool>? onConnectionChanged = null,
        Func<IWebSocketTransport>? transportFactory = null,
        TimeProvider? timeProvider = null,
        Action<ulong>? onServerVersionAdvanced = null,
        ReconnectPolicy? reconnectPolicy = null,
        ClipboardBridge? clipboard = null,
        SyncSession? session = null)
    {
        _config = config;
        _uiContext = uiContext;
        _onStatus = onStatus;
        _onRemoteTextApplied = onRemoteTextApplied;
        _onSessionExpired = onSessionExpired;
        _onConnectionChanged = onConnectionChanged;
        _transportFactory = transportFactory ?? (() => new ClientWebSocketTransport());
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onServerVersionAdvanced = onServerVersionAdvanced;
        _reconnectPolicy = reconnectPolicy ?? new ReconnectPolicy(timeProvider: _timeProvider);
        _clipboard = clipboard ?? new ClipboardBridge(_uiContext);
        // Status 作为 onStatus 回传：session 的状态输出仍经 UI 线程转发
        _session = session ?? new SyncSession(
            config, _clipboard, ForwardStatus, _onRemoteTextApplied, _onServerVersionAdvanced, _timeProvider);
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
            _reconnectPolicy.Reset();
        }
        _ = ConnectAsync();
    }

    // 停止引擎：以 close code 1000 正常关闭并取消所有异步操作，不再重连。
    // StopAsync/DisposeAsync 可能被重复调用，_cts 已释放时静默忽略
    public async Task StopAsync()
    {
        lock (_stateLock)
        {
            _stopped = true;
            _connected = false;
        }
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放：无需再取消
        }
        // 让在途重连任务尽快结束；旧任务 finally 也会复位该标志
        _reconnectPolicy.EndReconnect();
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    // 由 ClipboardMonitor 调用，把本地剪贴板新内容广播出去。
    // 同时刷新"本地剪贴板最后变更时刻"供 snapshot.localModifiedAtUtc 使用。
    public void SendLocalText(string text, string source)
    {
        _session.NotifyLocalChange();
        var client = _client;
        _ = Task.Run(() => _session.SendLocalTextAsync(text, source, client, _cts.Token));
    }

    // 电源恢复（SystemEvents.PowerModeChanged=Resume）或网络恢复
    // （NetworkChange.NetworkAvailabilityChanged）时由 App 层调用：
    // 若引擎处于退避等待，立即（1-2s 内）触发重连。
    public void NotifyWake()
    {
        if (IsStopped())
        {
            return;
        }
        _reconnectPolicy.NotifyWake();
    }

    // ---------- ISyncListener ----------

    // welcome 到达：重置重连退避；应用交给 SyncSession（latest 比本地新且非本端发出）
    public Task OnWelcomeAsync(WelcomeMessage welcome)
    {
        _reconnectPolicy.Reset();
        return _session.OnWelcomeAsync(welcome);
    }

    // clip 广播到达：交给 SyncSession（version 比本地游标新且非本端发出 → 应用）
    public Task OnClipAsync(InboundClipMessage message)
    {
        return _session.OnClipAsync(message);
    }

    // clip_ack：推进服务端版本游标（SyncSession）
    public Task OnClipAckAsync(ClipAckMessage ack)
    {
        return _session.OnClipAckAsync(ack);
    }

    // ping：立即回复 pong（clientTimeUtc 为 UTC RFC3339 Z）
    public Task OnPingAsync(PingMessage ping)
    {
        var client = _client;
        if (client is null)
        {
            return Task.CompletedTask;
        }
        try
        {
            return client.SendPongAsync(new PongMessage(JsonUtil.Rfc3339UtcNow(_timeProvider)), _cts.Token);
        }
        catch (Exception error)
        {
            // 发送失败由接收循环/看门狗兜底触发重连
            Logger.LogError("Failed to send pong.", error);
            return Task.CompletedTask;
        }
    }

    // bye：解析并记录 reason（如 server_shutdown）到日志，不影响重连决策。
    // 随后的关闭事件（close 1001）走温和退避
    public Task OnByeAsync(ByeMessage bye)
    {
        lock (_stateLock)
        {
            _sawBye = true;
        }
        Logger.Log($"Server sent bye: {bye.Reason ?? "no reason"}");
        return Task.CompletedTask;
    }

    // error 帧处理：交给 SyncSession（text_too_large/rate_limited 的 Status 由 session 回调处理）
    public Task OnErrorFrameAsync(ErrorMessage error)
    {
        return _session.OnErrorFrameAsync(error);
    }

    // 连接被远端关闭：bye/1001 走温和退避，其余按普通退避重连
    public Task OnClosedAsync(string reason, WebSocketCloseStatus? closeStatus)
    {
        bool gentle;
        lock (_stateLock)
        {
            gentle = _sawBye || closeStatus == WebSocketCloseStatus.EndpointUnavailable;
            _sawBye = false;
        }
        MarkDisconnected();
        Status(ErrorCodes.Disconnected, reason);
        ScheduleReconnect(gentle);
        return Task.CompletedTask;
    }

    // 传输层错误（网络断开、看门狗 Abort、消息超限等）：按普通退避重连
    public Task OnTransportErrorAsync(Exception error)
    {
        MarkDisconnected();
        Status(ErrorCodes.WebSocketError, error.Message);
        ScheduleReconnect(gentle: false);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    // ---------- 连接生命周期 ----------

    // 建立到服务端的 WebSocket 连接并发出 hello。
    // 返回值：true=无需由调用方继续重连（成功/关停/会话失效/致命错误），
    //         false=需要继续退避重连。
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

            // token 距过期不足余量：直接走会话恢复，不再建立 WebSocket
            if (!EnsureTokenValid())
            {
                return await HandleSessionExpiredAsync_ForConnect().ConfigureAwait(false);
            }

            Status(ErrorCodes.Connecting);
            await DisposeOldClientAsync().ConfigureAwait(false);

            var client = await CreateAndOpenClientAsync().ConfigureAwait(false);
            if (client is null)
            {
                // 关停竞态：连接已释放，由调用方结束重连
                return true;
            }

            await SendHelloAndMarkConnectedAsync(client).ConfigureAwait(false);
            return true;
        }
        catch (SessionExpiredException)
        {
            return await HandleSessionExpiredAsync_ForConnect().ConfigureAwait(false);
        }
        catch (FatalProtocolException error)
        {
            return await HandleFatalAsync(error).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            await DisposeClientAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            return await HandleTransientAsync(error).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // token 是否仍有效（未临期）。token 距过期不足余量视为失效，直接走重登
    private bool EnsureTokenValid()
    {
        return !TokenNearlyExpired();
    }

    // 关闭并释放可能残留的旧连接
    private async Task DisposeOldClientAsync()
    {
        var oldClient = Interlocked.Exchange(ref _client, null);
        if (oldClient is not null)
        {
            await oldClient.DisposeAsync().ConfigureAwait(false);
        }
    }

    // 构造并建立新连接；若发生关停竞态则释放并返回 null
    private async Task<SyncClient?> CreateAndOpenClientAsync()
    {
        var client = new SyncClient(
            _config,
            _config.AuthToken,
            this,
            _transportFactory,
            _timeProvider);
        if (IsStopped())
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        _client = client;
        await client.ConnectAsync(_cts.Token).ConfigureAwait(false);
        return client;
    }

    // 建连成功后发送 hello，并置位连接状态
    private async Task SendHelloAndMarkConnectedAsync(SyncClient client)
    {
        await SendHelloAsync(client).ConfigureAwait(false);
        // 把当前连接的发送通道挂到 session，供本地发送使用
        _session.SetConnected(true);
        lock (_stateLock)
        {
            _connected = true;
        }
        Status(ErrorCodes.Connected);
        _onConnectionChanged?.Invoke(true);
    }

    // 认证失效（token 临期或 401/403）：不自动重连，交给 App 层决定是否静默重登
    private async Task<bool> HandleSessionExpiredAsync_ForConnect()
    {
        await DisposeClientAsync().ConfigureAwait(false);
        MarkDisconnected();
        Status(ErrorCodes.SessionExpiredPleaseLogin);
        if (_onSessionExpired is not null)
        {
            await _onSessionExpired().ConfigureAwait(false);
        }
        return true;
    }

    // 子协议协商失败（HTTP 400）等致命错误：记录日志并停止重连
    private async Task<bool> HandleFatalAsync(FatalProtocolException error)
    {
        await DisposeClientAsync().ConfigureAwait(false);
        MarkDisconnected();
        Logger.LogError("Fatal protocol error; reconnect suspended.", error);
        Status(ErrorCodes.FatalProtocolError, error.Message);
        return true;
    }

    // 传输层等一般错误：按普通退避重连
    private async Task<bool> HandleTransientAsync(Exception error)
    {
        await DisposeClientAsync().ConfigureAwait(false);
        await OnTransportErrorAsync(error).ConfigureAwait(false);
        return false;
    }

    // 发送 hello。snapshot 为当前本地剪贴板文本（非空且未超限时附带）
    private async Task SendHelloAsync(SyncClient client)
    {
        var snapshot = await _session.BuildHelloSnapshotAsync().ConfigureAwait(false);
        var hello = new HelloMessage(_config.ClientId, _config.ClientName, _session.LastServerVersion, snapshot);
        await client.SendHelloAsync(hello, _cts.Token).ConfigureAwait(false);
    }

    // 释放当前 SyncClient。若 StopAsync 已抢先换走并释放，这里不再触碰。
    private async Task DisposeClientAsync()
    {
        var client = _client;
        if (client is null)
        {
            return;
        }
        var current = Interlocked.CompareExchange(ref _client, null, client);
        if (!ReferenceEquals(current, client))
        {
            return;
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

    // ---------- 重连调度 ----------

    // 调度下一次重连尝试，带退避；同一时刻最多一个重连任务在途
    private void ScheduleReconnect(bool gentle)
    {
        if (IsStopped())
        {
            return;
        }

        if (!_reconnectPolicy.TryBeginReconnect(out var delay, gentle))
        {
            return;
        }

        Status(ErrorCodes.Connecting);
        _ = Task.Run(async () =>
        {
            var shouldRetry = false;
            try
            {
                await _reconnectPolicy.WaitForDelayAsync(_cts.Token).ConfigureAwait(false);
                shouldRetry = await ConnectAsync().ConfigureAwait(false) is false;
            }
            catch (OperationCanceledException)
            {
                // 正常关停
            }
            catch (ObjectDisposedException)
            {
                // 关停竞态：_cts 已释放
            }
            finally
            {
                _reconnectPolicy.EndReconnect();
            }

            // ConnectAsync 失败路径里 OnTransportErrorAsync 的重连触发被单飞吞掉，
            // 这里在标志复位后补一次，保证退避循环继续；成功连接则不再调度
            if (shouldRetry)
            {
                ScheduleReconnect(gentle);
            }
        });
    }

    private bool TokenNearlyExpired()
    {
        var expires = _config.TokenExpiresAtUtc;
        return expires is { } expiry && _timeProvider.GetUtcNow().UtcDateTime >= expiry - TokenExpiryMargin;
    }

    // ---------- 状态与 UI 辅助 ----------

    // 标记为已断开（含 session 发送门），并通知连接状态回调
    private void MarkDisconnected()
    {
        _session.SetConnected(false);
        bool wasConnected;
        lock (_stateLock)
        {
            wasConnected = _connected;
            _connected = false;
        }
        if (wasConnected)
        {
            _onConnectionChanged?.Invoke(false);
        }
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

    // 状态消息到 UI 线程的最终转发（Action<string> 兼容，供 SyncSession 作为 onStatus 使用）。
    // SyncSession 传入的是已用 CoreStatus.Pack 打包好的信封字符串，这里直接透传，不二次打包。
    private void ForwardStatus(string message)
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

    // 领域码 + 参数 → 打包为状态信封后转发到 UI 线程显示。
    private void Status(string code, params object?[] args)
    {
        var msg = args is { Length: > 0 } ? CoreStatus.Pack(code, args) : CoreStatus.Pack(code);
        ForwardStatus(msg);
    }
}


