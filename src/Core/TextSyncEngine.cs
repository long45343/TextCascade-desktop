using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TextCascadeSharp.Core;

// 剪贴板同步核心引擎。实现 ISyncListener 接收 textcascade.v1 协议事件，
// 并通过 _uiContext 把需要在 UI 线程执行的操作（读写剪贴板）转发回主线程。
//
// 状态机：
//   stopped --Start()--> connecting --hello 发出--> connected
//   任意状态 --传输错误/远端关闭--> disconnected --ScheduleReconnect--> connecting
//
// 去重机制：
//   _lastSentHashHex   最近一次成功发出的本地内容 hash（防远端回环）
//   _lastRemoteHashHex 最近一次成功应用的远端内容 hash（防本地回环）
//   _suppressNextLocal 远端写入本地剪贴板后，跳过因此触发的下一次本地通知
//   _lastServerVersion 服务端版本游标（welcome/clip/clip_ack 推进，防重复）
//
// 退避策略（收到 welcome 后重置）：
//   普通断开：1s、2s、5s、10s、30s、60s，之后固定 60s
//   维护断开（bye/close 1001）：1s、2s、5s、10s，之后固定 10s
public sealed class TextSyncEngine : ISyncListener, IAsyncDisposable
{
    // 普通断开重连退避序列
    private static readonly TimeSpan[] NormalBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ];

    // 服务端维护（bye/close 1001）温和退避序列
    private static readonly TimeSpan[] GentleBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    // token 距 expiresAtUtc 不足该余量时视为即将过期，直接走重登
    private static readonly TimeSpan TokenExpiryMargin = TimeSpan.FromSeconds(30);

    // rate_limited 错误后的本地发送暂停时长
    private static readonly TimeSpan RateLimitPause = TimeSpan.FromSeconds(1);

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
    private readonly CancellationTokenSource _cts = new();
    // 保护 _stopped/_connected/_lastServerVersion/_lastSentHashHex/_lastRemoteHashHex/
    // _suppressNextLocal/_reconnectAttempts/_sawBye/_sendPausedUntil
    private readonly object _stateLock = new();
    // 串行化 Start() 与重连任务的连接建立，避免双会话并发
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private SyncClient? _client;
    private bool _stopped = true;
    private bool _connected;
    // 自上次 welcome 后的重连尝试次数（决定退避档位）
    private int _reconnectAttempts;
    // 最近已知服务端版本
    private ulong _lastServerVersion;
    // 最近一次成功发出的本地内容 hash（hex）
    private string? _lastSentHashHex;
    // 最近一次成功应用的远端内容 hash（hex）
    private string? _lastRemoteHashHex;
    // 远端写入本地剪贴板后置 true，跳过下一次本地通知
    private bool _suppressNextLocal;
    // 0=无重连在途，1=有（Interlocked 单飞）
    private int _reconnectInFlight;
    // 收到 bye 后置位，随后的关闭事件走温和退避
    private bool _sawBye;
    // rate_limited 暂停发送截止时刻（_timeProvider 时间）
    private DateTimeOffset _sendPausedUntil = DateTimeOffset.MinValue;
    // 本地剪贴板最后变更时刻（snapshot.localModifiedAtUtc 用）
    private DateTimeOffset _lastLocalChangeUtc = DateTimeOffset.UtcNow;
    // 电源/网络恢复唤醒信号
    private CancellationTokenSource _wakeCts = new();

    // 测试接缝：覆盖重连退避；生产路径为 null 使用默认策略
    internal TimeSpan? ReconnectDelayOverride { get; set; }

    // 测试接缝：替换 Clipboard.SetText 写实现；生产路径为 null
    internal Func<string, CancellationToken, Task>? ClipboardSetAsync { get; set; }

    // 测试接缝：替换剪贴板读实现（hello snapshot 用）；生产路径为 null
    internal Func<string>? ClipboardGetAsync { get; set; }

    internal TextSyncEngine(
        ClipConfig config,
        SynchronizationContext uiContext,
        Action<string> onStatus,
        Action<string> onRemoteTextApplied,
        Func<Task>? onSessionExpired = null,
        Action<bool>? onConnectionChanged = null,
        Func<IWebSocketTransport>? transportFactory = null,
        TimeProvider? timeProvider = null,
        Action<ulong>? onServerVersionAdvanced = null)
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
        _lastServerVersion = config.LastServerVersion;
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
        Interlocked.Exchange(ref _reconnectInFlight, 0);
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    // 由 ClipboardMonitor 调用，把本地剪贴板新内容广播出去。
    // 同时刷新“本地剪贴板最后变更时刻”供 snapshot.localModifiedAtUtc 使用。
    public void SendLocalText(string text, string source)
    {
        _lastLocalChangeUtc = _timeProvider.GetUtcNow();
        _ = Task.Run(() => SendLocalTextAsync(text, source, _cts.Token));
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
        var old = Interlocked.Exchange(ref _wakeCts, new CancellationTokenSource());
        try
        {
            old.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放说明无在途等待
        }
        old.Dispose();
    }

    // ---------- ISyncListener ----------

    // welcome 到达：重置重连退避；latest 比本地新且非本端发出 → 应用
    public Task OnWelcomeAsync(WelcomeMessage welcome)
    {
        lock (_stateLock)
        {
            _reconnectAttempts = 0;
        }
        if (welcome.Latest is not { } latest)
        {
            return Task.CompletedTask;
        }
        return ApplyRemoteTextAsync(latest.Version, latest.Payload, latest.Encrypted, latest.Hash);
    }

    // clip 广播到达：version 比本地游标新且非本端发出 → 应用。
    // 最新值语义：慢设备延迟到达的旧 clip 也会获得新版本并覆盖当前值，
    // 不做额外防御（version/hash 去重仅防回环与重复）
    public Task OnClipAsync(InboundClipMessage message)
    {
        return ApplyRemoteTextAsync(message.Version, message.Payload, message.Encrypted, message.Hash);
    }

    // clip_ack：推进服务端版本游标
    public Task OnClipAckAsync(ClipAckMessage ack)
    {
        AdvanceServerVersion(ack.Version);
        return Task.CompletedTask;
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

    // error 帧处理表（详见 spec“错误码处理表”）：
    //   invalid_message / empty_text / hello_timeout / frame_too_large / server_busy
    //     → 仅记日志，连接保持；frame_too_large/hello_timeout 由服务端
    //       随后的 1009/1008 关闭触发普通退避；server_busy 由看门狗兜底
    //   text_too_large → 丢弃该文本并状态提示
    //   rate_limited   → 本地暂停发送约 1s 并状态提示
    public Task OnErrorFrameAsync(ErrorMessage error)
    {
        switch (error.Code)
        {
            case "invalid_message":
                Logger.Log($"Server reported invalid_message: {error.Message ?? "-"}");
                break;
            case "text_too_large":
                Status(UiText.TextTooLargeIgnored);
                break;
            case "empty_text":
                Logger.Log("Server reported empty_text; outbound self-check should prevent this.");
                break;
            case "rate_limited":
                lock (_stateLock)
                {
                    _sendPausedUntil = _timeProvider.GetUtcNow() + RateLimitPause;
                }
                Status(UiText.RateLimitedPaused);
                break;
            case "hello_timeout":
                Logger.Log("Server reported hello_timeout; server will close with 1008.");
                break;
            case "frame_too_large":
                Logger.Log("Server reported frame_too_large; server will close with 1009.");
                break;
            case "server_busy":
                // 不依赖该帧（服务端不保证送达）：由接收看门狗/断链检测触发重连兜底
                Logger.Log("Server reported server_busy.");
                break;
            default:
                Logger.Log($"Server reported unknown error '{error.Code}': {error.Message ?? "-"}");
                break;
        }
        return Task.CompletedTask;
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
        Status(UiText.Disconnected(reason));
        ScheduleReconnect(gentle);
        return Task.CompletedTask;
    }

    // 传输层错误（网络断开、看门狗 Abort、消息超限等）：按普通退避重连
    public Task OnTransportErrorAsync(Exception error)
    {
        MarkDisconnected();
        Status(UiText.WebSocketError(error.Message));
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

            // token 距过期不足：直接走会话恢复，不再建立 WebSocket
            if (TokenNearlyExpired())
            {
                MarkDisconnected();
                Status(UiText.SessionExpiredPleaseLogin);
                if (_onSessionExpired is not null)
                {
                    await _onSessionExpired().ConfigureAwait(false);
                }
                return true;
            }

            Status(UiText.Connecting);
            // 关闭并释放可能残留的旧连接
            var oldClient = Interlocked.Exchange(ref _client, null);
            if (oldClient is not null)
            {
                await oldClient.DisposeAsync().ConfigureAwait(false);
            }

            var client = new SyncClient(
                _config,
                _config.AuthToken,
                this,
                _transportFactory,
                _timeProvider);
            if (IsStopped())
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return true;
            }

            _client = client;
            await client.ConnectAsync(_cts.Token).ConfigureAwait(false);
            // 建连成功即发 hello（helloTimeoutSeconds 内必须发出）
            await SendHelloAsync(client).ConfigureAwait(false);

            lock (_stateLock)
            {
                _connected = true;
            }
            Status(UiText.Connected);
            _onConnectionChanged?.Invoke(true);
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
        catch (FatalProtocolException error)
        {
            // 子协议协商失败（HTTP 400）等致命错误：记录日志并停止重连
            await DisposeClientAsync().ConfigureAwait(false);
            MarkDisconnected();
            Logger.LogError("Fatal protocol error; reconnect suspended.", error);
            Status(error.Message);
            return true;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            await DisposeClientAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            await DisposeClientAsync().ConfigureAwait(false);
            await OnTransportErrorAsync(error).ConfigureAwait(false);
            return false;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // 发送 hello。snapshot 为当前本地剪贴板文本（非空且未超限时附带）
    private async Task SendHelloAsync(SyncClient client)
    {
        HelloSnapshot? snapshot = null;
        var text = await ReadClipboardAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(text) && IsWithinLimits(text, UiText.DirectionOutbound))
        {
            var hashHex = HashUtil.Fnv1A64Hex(text);
            var payload = text;
            var encrypted = false;
            if (_config.CipherEnabled && _config.DerivedKeyBase64.Length > 0)
            {
                payload = JsonUtil.EncryptedPayload(CryptoManager.Encrypt(text, _config.DerivedKeyBase64));
                encrypted = true;
            }
            snapshot = new HelloSnapshot(payload, encrypted, hashHex, JsonUtil.Rfc3339Utc(_lastLocalChangeUtc.UtcDateTime));
            // 快照视为本端当前值：记录 hash 防止服务端广播回环
            lock (_stateLock)
            {
                _lastSentHashHex = hashHex;
            }
        }
        var hello = new HelloMessage(_config.ClientId, _config.ClientName, _lastServerVersion, snapshot);
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

    // ---------- 远端文本应用 ----------

    // welcome.latest 与入站 clip 的公共处理：
    //   1) version 游标去重（≤ 游标直接跳过并推进游标）
    //   2) hash 与最近本地发出 hash 相同 → 仅推进游标（回环防护）
    //   3) 解密（如加密）
    //   4) 大小校验（失败不修改游标）
    //   5) 写入本地剪贴板（带短退避重试，最终失败走 cmd 兜底）
    //   6) 写入成功后才推进游标、记录远端 hash 并抑制下一次本地事件
    private async Task ApplyRemoteTextAsync(ulong version, string payload, bool encrypted, string hashHex)
    {
        try
        {
            lock (_stateLock)
            {
                if (version <= _lastServerVersion)
                {
                    return;
                }
                if (hashHex.Equals(_lastSentHashHex, StringComparison.Ordinal))
                {
                    // 本端发出的内容回环：只推进游标，不写剪贴板
                    _lastServerVersion = version;
                    _onServerVersionAdvanced?.Invoke(version);
                    return;
                }
            }

            var text = encrypted
                ? CryptoManager.Decrypt(JsonUtil.ParseEncryptedPayload(payload), _config.DerivedKeyBase64)
                : payload;

            if (!IsWithinLimits(text, UiText.DirectionInbound))
            {
                return;
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
                        _lastServerVersion = version;
                        _lastRemoteHashHex = hashHex;
                        _suppressNextLocal = true;
                    }
                    _onServerVersionAdvanced?.Invoke(version);
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

    // ---------- 本地文本发送 ----------

    // 处理本地剪贴板新内容：
    //   1) 若 _suppressNextLocal 为 true（远端写入触发的本地通知），跳过
    //   2) 检查连接状态与限流暂停
    //   3) 检查大小
    //   4) hash 与最近远端 hash 去重
    //   5) 加密并发送 clip（帧大小自检）
    //   6) 仅在发送成功后才记录 _lastSentHashHex
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
            if (_timeProvider.GetUtcNow() < _sendPausedUntil)
            {
                // rate_limited 后的本地暂停窗口
                return;
            }
        }

        if (!IsWithinLimits(text, UiText.DirectionOutbound))
        {
            return;
        }

        var hashHex = HashUtil.Fnv1A64Hex(text);
        lock (_stateLock)
        {
            if (hashHex.Equals(_lastRemoteHashHex, StringComparison.Ordinal))
            {
                // 与最近应用的远端内容相同：无需广播
                return;
            }
        }

        var payload = text;
        var encrypted = false;
        if (_config.CipherEnabled && _config.DerivedKeyBase64.Length > 0)
        {
            payload = JsonUtil.EncryptedPayload(CryptoManager.Encrypt(text, _config.DerivedKeyBase64));
            encrypted = true;
        }

        var clip = new OutboundClipMessage(Guid.NewGuid().ToString("N"), payload, encrypted, hashHex);
        var json = JsonUtil.Clip(clip);
        // 发送侧自检帧大小，避免触发服务端 frame_too_large/1009
        if (Encoding.UTF8.GetByteCount(json) > SyncClient.MaxWebSocketMessageBytes)
        {
            Status(UiText.ClipboardTooLarge(UiText.DirectionOutbound, Encoding.UTF8.GetByteCount(json)));
            return;
        }

        var client = _client;
        if (client is null)
        {
            return;
        }

        try
        {
            await client.SendClipAsync(clip, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常关停：不再抛出，避免火忘任务产生未观察异常
        }
        catch (Exception error)
        {
            // 发送失败：不提交 hash，下次相同内容仍可重试
            Status(UiText.WebSocketError(error.Message));
            return;
        }

        // 发送成功后才提交 hash，避免失败时被静默丢弃
        lock (_stateLock)
        {
            _lastSentHashHex = hashHex;
        }
        Status(UiText.Broadcasting);
    }

    // ---------- 重连调度 ----------

    // 调度下一次重连尝试，带退避；同一时刻最多一个重连任务在途
    private void ScheduleReconnect(bool gentle)
    {
        if (IsStopped())
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _reconnectInFlight, 1, 0) != 0)
        {
            return;
        }

        var attempt = 0;
        lock (_stateLock)
        {
            _reconnectAttempts++;
            attempt = _reconnectAttempts;
        }
        var delay = ReconnectDelay();
        Status(UiText.Connecting);
        _ = Task.Run(async () =>
        {
            var shouldRetry = false;
            try
            {
                await DelayInterruptibleAsync(delay).ConfigureAwait(false);
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
                Interlocked.Exchange(ref _reconnectInFlight, 0);
            }

            // ConnectAsync 失败路径里 OnTransportErrorAsync 的重连触发被单飞吞掉，
            // 这里在标志复位后补一次，保证退避循环继续；成功连接则不再调度
            if (shouldRetry)
            {
                ScheduleReconnect(gentle);
            }
        });

        // 退避档位：本次断开的温和/普通属性由序列选择体现
        TimeSpan ReconnectDelay()
        {
            if (ReconnectDelayOverride is { } custom)
            {
                return custom;
            }
            return BackoffDelay(gentle, attempt);
        }
    }

    // 退避序列查表：attempt 从 1 开始，超出序列长度后取最后一档
    internal static TimeSpan BackoffDelay(bool gentle, int attempt)
    {
        var sequence = gentle ? GentleBackoff : NormalBackoff;
        var index = attempt <= 1 ? 0 : attempt - 1;
        return index < sequence.Length ? sequence[index] : sequence[^1];
    }

    // 可被唤醒信号打断的退避等待。唤醒（电源/网络恢复）后延时 1s 让网络
    // 稳定，随后立即重连，不等待当前退避到期
    private async Task DelayInterruptibleAsync(TimeSpan delay)
    {
        var wakeCts = _wakeCts;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, wakeCts.Token);
        try
        {
            await Task.Delay(delay, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            // 被唤醒信号打断：稍等 1s 后由调用方立即重连
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常关停
            }
        }
    }

    private bool TokenNearlyExpired()
    {
        var expires = _config.TokenExpiresAtUtc;
        return expires is { } expiry && _timeProvider.GetUtcNow().UtcDateTime >= expiry - TokenExpiryMargin;
    }

    // ---------- 状态与 UI 辅助 ----------

    // 推进服务端版本游标（仅前进），并通过回调通知 App 层持久化。
    // spec 约定：lastServerVersion 持久化存储为无符号整数，重启后用于
    // hello 与去重判断，避免本设备早前发出的内容在重启后被重新应用
    private void AdvanceServerVersion(ulong version)
    {
        lock (_stateLock)
        {
            if (version <= _lastServerVersion)
            {
                return;
            }
            _lastServerVersion = version;
        }
        _onServerVersionAdvanced?.Invoke(version);
    }

    // 检查内容字节数是否在服务端和本地限制内
    private bool IsWithinLimits(string text, string direction)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        var localLimit = _config.LocalMaxClipboardBytes > 0 ? _config.LocalMaxClipboardBytes : _config.MaxTextBytes;
        var ok = bytes <= _config.MaxTextBytes && bytes <= localLimit;
        if (!ok)
        {
            Status(UiText.ClipboardTooLarge(direction, bytes));
        }
        return ok;
    }

    // 标记为已断开，并通知连接状态回调
    private void MarkDisconnected()
    {
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

    // 读取本地剪贴板文本（hello snapshot 用）。失败返回空串
    private async Task<string> ReadClipboardAsync()
    {
        if (ClipboardGetAsync is { } fake)
        {
            return await Task.Run(fake).ConfigureAwait(false);
        }
        try
        {
            var text = string.Empty;
            await InvokeUiAsync(() =>
            {
                text = Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    ? Clipboard.GetText(TextDataFormat.UnicodeText)
                    : string.Empty;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            return text;
        }
        catch
        {
            // 剪贴板被占用等情况：本次不带 snapshot
            return string.Empty;
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

    // cmd 兜底：通过 clip 从标准输入写入文本
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
