using System.Runtime.InteropServices;
using System.Text;

namespace TextCascadeSharp.Core;

// 协议消息的领域语义：welcome/clip/clip_ack/error 的应用、去重、回环抑制、
// 发送暂停、加解密衔接、大小限制与本地发送。Engine 只负责连接生命周期与
// 协议回调分发，剪贴板读写经 ClipboardBridge 完成。
public sealed class SyncSession
{
    // rate_limited 错误后的本地发送暂停时长
    private static readonly TimeSpan RateLimitPause = TimeSpan.FromSeconds(1);

    // 服务端帧上限与本端文本上限之间的差额（用于协议头与 JSON 转义）。
    // 服务端默认 max_frame_bytes 589824 - max_text_bytes 524288 = 65536。
    // 登录响应不下发帧上限，因此这是按服务端默认配置取的保守值。
    private const int FrameOverheadBytes = 64 * 1024;

    private readonly ClipConfig _config;
    private readonly ClipboardBridge _clipboard;
    private readonly Action<string> _onStatus;
    private readonly Action<string> _onRemoteTextApplied;
    private readonly Action<ulong>? _onServerVersionAdvanced;
    private readonly TimeProvider _timeProvider;
    private readonly object _stateLock = new();

    private ulong _lastServerVersion;
    private string? _lastSentHashHex;
    private string? _lastRemoteHashHex;
    private bool _connected;
    // rate_limited 暂停发送截止时刻（_timeProvider 时间）
    private DateTimeOffset _sendPausedUntil = DateTimeOffset.MinValue;
    // 本地剪贴板最后变更时刻（snapshot.localModifiedAtUtc 用）
    private readonly Func<ISyncTransportSender?>? _currentSenderProvider;
    // 暂停状态来源（手动暂停或 UU 规避）：后台重试期间必须据此停止改写剪贴板
    private readonly Func<bool>? _isPaused;
    private DateTimeOffset _lastLocalChangeUtc;
    private DateTimeOffset _lastRemoteAppliedUtc = DateTimeOffset.MinValue;
    private PendingLocal? _pendingLocal;
    private CancellationTokenSource? _backgroundCts = new();

    private sealed record PendingLocal(string Text, string Source, DateTimeOffset StoredUtc);

    // 首次写入的短退避：只处理瞬时占用（另一进程刚打开剪贴板）。
    // 更长时间的占用交给下面的后台重试，两层都在本类内，判定与写入不跨 await。
    internal static TimeSpan ShortRetryDelay = TimeSpan.FromMilliseconds(100);
    internal static int ShortRetryAttempts = 5;

    // 短退避全部失败后的后台重试档位
    internal static TimeSpan[] ApplyRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    public SyncSession(
        ClipConfig config,
        ClipboardBridge clipboard,
        Action<string> onStatus,
        Action<string> onRemoteTextApplied,
        Action<ulong>? onServerVersionAdvanced = null,
        TimeProvider? timeProvider = null,
        Func<ISyncTransportSender?>? currentSenderProvider = null,
        Func<bool>? isPaused = null)
    {
        _config = config;
        _clipboard = clipboard;
        _onStatus = onStatus;
        _onRemoteTextApplied = onRemoteTextApplied;
        _onServerVersionAdvanced = onServerVersionAdvanced;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _currentSenderProvider = currentSenderProvider;
        _isPaused = isPaused;
        _lastLocalChangeUtc = _timeProvider.GetUtcNow();
        _lastServerVersion = config.LastServerVersion;
    }

    private bool IsPaused => _isPaused?.Invoke() == true;

    public ulong LastServerVersion
    {
        get
        {
            lock (_stateLock)
            {
                return _lastServerVersion;
            }
        }
    }


    // 切换本地发送门（连接状态）
    public void SetConnected(bool connected)
    {
        lock (_stateLock)
        {
            _connected = connected;
        }
    }

    internal void CancelBackgroundWork()
    {
        try
        {
            _backgroundCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // 记录本地剪贴板最后变更时刻（hello snapshot.localModifiedAtUtc 使用）
    public void NotifyLocalChange()
    {
        lock (_stateLock)
        {
            _lastLocalChangeUtc = _timeProvider.GetUtcNow();
        }
    }

    // welcome 到达：latest 比本地新且非本端发出 → 应用
    public Task OnWelcomeAsync(WelcomeMessage welcome)
    {
        if (welcome.Latest is not { } latest)
        {
            return Task.CompletedTask;
        }
        return ApplyRemoteTextAsync(latest.Version, latest.Payload, latest.Encrypted, latest.Hash);
    }

    // clip 广播到达：version 比本地游标新且非本端发出 → 应用
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

    // error 帧处理表（详见 spec“错误码处理表”）
    public Task OnErrorFrameAsync(ErrorMessage error)
    {
        switch (error.Code)
        {
            case "invalid_message":
                Logger.Log($"Server reported invalid_message: {error.Message ?? "-"}");
                break;
            case "text_too_large":
                _onStatus(CoreStatus.Pack(ErrorCodes.TextTooLargeIgnored));
                break;
            case "empty_text":
                Logger.Log("Server reported empty_text; outbound self-check should prevent this.");
                break;
            case "rate_limited":
                lock (_stateLock)
                {
                    _sendPausedUntil = _timeProvider.GetUtcNow() + RateLimitPause;
                }
                _onStatus(CoreStatus.Pack(ErrorCodes.RateLimitedPaused));
                break;
            case "hello_timeout":
                Logger.Log("Server reported hello_timeout; server will close with 1008.");
                break;
            case "frame_too_large":
                Logger.Log("Server reported frame_too_large; server will close with 1009.");
                break;
            case "server_busy":
                Logger.Log("Server reported server_busy.");
                break;
            default:
                Logger.Log($"Server reported unknown error '{error.Code}': {error.Message ?? "-"}");
                break;
        }
        return Task.CompletedTask;
    }

    // 构建 hello 快照：读取本地剪贴板，非空且未超限时加密并记录本端 hash（防回环）。
    // 快照超限时返回 null 正常连上，而不是带一个必定被服务端拒绝的 snapshot ——
    // 预 hello 阶段的解析失败会被服务端统一包装成 invalid_message + 1008 关闭，
    // 重连又会读到同一份剪贴板，形成"连上就被踢"的循环。
    public async Task<HelloSnapshot?> BuildHelloSnapshotAsync()
    {
        var text = await _clipboard.ReadTextAsync(CancellationToken.None).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var (payload, encrypted) = BuildPayload(text);
        if (_config.CipherEnabled && !encrypted)
        {
            // 加密已开启却缺密钥：连带快照也不发，避免明文进握手帧
            _onStatus(CoreStatus.Pack(ErrorCodes.EncryptionKeyMissing));
            return null;
        }
        var hashHex = HashUtil.Fnv1A64Hex(text);
        DateTimeOffset localChangeUtc;
        lock (_stateLock)
        {
            localChangeUtc = _lastLocalChangeUtc;
        }

        var snapshot = new HelloSnapshot(payload, encrypted, hashHex, JsonUtil.Rfc3339Utc(localChangeUtc.UtcDateTime));
        var hello = new HelloMessage(_config.ClientId, _config.ClientName, LastServerVersion, snapshot);
        if (!IsWithinOutboundLimits(payload, JsonUtil.Hello(hello)))
        {
            Logger.Log("BuildHelloSnapshotAsync: local clipboard exceeds server limits; connecting without snapshot.");
            return null;
        }

        // 快照视为本端当前值：记录 hash 防止服务端广播回环
        lock (_stateLock)
        {
            _lastSentHashHex = hashHex;
        }
        return snapshot;
    }

    // 加密开启且密钥可用时生成加密 payload，否则原样发送明文
    private (string Payload, bool Encrypted) BuildPayload(string text)
    {
        if (_config.CipherEnabled && _config.DerivedKeyBase64.Length > 0)
        {
            return (JsonUtil.EncryptedPayload(CryptoManager.Encrypt(text, _config.DerivedKeyBase64)), true);
        }
        return (text, false);
    }

    public async Task SendLocalTextAsync(string text, string source, ISyncTransportSender? sender, CancellationToken cancellationToken)
    {
        await SendLocalTextInternalAsync(text, source, sender, isRetry: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendLocalTextInternalAsync(string text, string source, ISyncTransportSender? sender, bool isRetry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        lock (_stateLock)
        {
            if (!_connected)
            {
                _pendingLocal = new PendingLocal(text, source, _timeProvider.GetUtcNow());
                _onStatus(CoreStatus.Pack(ErrorCodes.IgnoredNotConnected, source));
                return;
            }
            if (_timeProvider.GetUtcNow() < _sendPausedUntil)
            {
                // rate_limited 后的本地暂停窗口
                _onStatus(CoreStatus.Pack(ErrorCodes.RateLimitedDropped));
                Logger.Log("SendLocalTextAsync: dropped local clipboard text due to active rate limit pause window.");
                return;
            }
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

        var (payload, encrypted) = BuildPayload(text);
        if (_config.CipherEnabled && !encrypted)
        {
            // 加密已开启却没有可用密钥：绝不退回明文发送。
            // 正常路径下 StartService 会提前拦下这种配置，这里是最后一道闸。
            _onStatus(CoreStatus.Pack(ErrorCodes.EncryptionKeyMissing));
            return;
        }
        var clip = new OutboundClipMessage(Guid.NewGuid().ToString("N"), payload, encrypted, hashHex);
        var json = JsonUtil.Clip(clip);
        // 按最终序列化结果校验：加密与 JSON 转义都会让字节数显著膨胀，
        // 只量明文会放过一批服务端必定拒绝的消息。
        if (!IsWithinOutboundLimits(payload, json))
        {
            return;
        }

        if (sender is null)
        {
            return;
        }

        try
        {
            await sender.SendClipAsync(clip, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常关停：不再抛出，避免火忘任务产生未观察异常
            return;
        }
        catch (Exception error) when (!isRetry && (error is InvalidOperationException or System.Net.WebSockets.WebSocketException))
        {
            if (_currentSenderProvider?.Invoke() is { } newSender && !ReferenceEquals(newSender, sender))
            {
                Logger.Log("SendLocalTextAsync: sender was stale; retrying once with current sender.");
                await SendLocalTextInternalAsync(text, source, newSender, isRetry: true, cancellationToken).ConfigureAwait(false);
                return;
            }
            // 发送失败：不提交 hash，下次相同内容仍可重试
            _onStatus(CoreStatus.Pack(ErrorCodes.WebSocketError, error.Message));
            return;
        }
        catch (Exception error)
        {
            // 发送失败：不提交 hash，下次相同内容仍可重试
            _onStatus(CoreStatus.Pack(ErrorCodes.WebSocketError, error.Message));
            return;
        }

        // 发送成功后才提交 hash，避免失败时被静默丢弃
        lock (_stateLock)
        {
            _lastSentHashHex = hashHex;
        }
        _onStatus(CoreStatus.Pack(ErrorCodes.Broadcasting));
    }

    // welcome.latest 与入站 clip 的公共处理：
    //   1) version 游标去重（≤ 游标直接跳过并推进游标）
    //   2) hash 与最近本地发出 hash 相同 → 仅推进游标（回环防护）
    //   3) 解密（如加密）
    //   4) 大小校验（失败不修改游标）
    //   5) 写入本地剪贴板（带短退避重试，最终失败走 cmd 兜底）
    //   6) 写入前登记远端 hash（防自写回环），写入成功后才推进游标
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

            if (!IsWithinInboundLimits(text))
            {
                return;
            }

            // 计算解密文本的本地实际 hash（防自写回环）
            var computedHashHex = HashUtil.Fnv1A64Hex(text);

            await ApplyWithRetryAsync(version, text, computedHashHex, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _onStatus(CoreStatus.Pack(ErrorCodes.InboundError, error.Message));
        }
    }

    // 写入本地剪贴板的唯一入口：短退避重试与后台重试都在这里，且每次写入前
    // 在同一个锁内完成"版本判定 + 记录远端 hash + 提交版本"，避免检查与写入之间
    // 被更新的入站消息插入（旧版本覆盖新版本、持久化游标回退）。
    //
    // 首次调用最多尝试 ShortRetryAttempts 次短退避；全部失败后转入后台长退避，
    // 每次重试前都重新检查版本是否已过期、以及是否已被暂停。
    private async Task ApplyWithRetryAsync(ulong version, string text, string hashHex, CancellationToken cancellationToken)
    {
        // 短退避：处理另一进程刚打开剪贴板的瞬时占用
        for (var attempt = 0; attempt < ShortRetryAttempts; attempt++)
        {
            if (IsPaused)
            {
                Logger.Log($"Apply: paused; skipping write for version {version}.");
                return;
            }

            if (await TryApplyOnceAsync(version, text, hashHex, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (attempt == ShortRetryAttempts - 1)
            {
                break;
            }

            try
            {
                await Task.Delay(ShortRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        _onStatus(CoreStatus.Pack(ErrorCodes.ClipboardWriteFailed, "Clipboard remains locked."));
        var token = _backgroundCts?.Token ?? CancellationToken.None;
        _ = RetryApplyAsync(version, text, hashHex, token);
    }

    // 单轮"检查-写入-提交"。返回是否成功写入。
    private async Task<bool> TryApplyOnceAsync(ulong version, string text, string hashHex, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (version <= _lastServerVersion)
            {
                Logger.Log($"Apply: version {version} already superseded (cursor {_lastServerVersion}), skipping write.");
                return true;
            }
            // 写前登记远端 hash：随后的监听回调据此识别为自写回显
            _lastRemoteHashHex = hashHex;
        }

        bool written;
        try
        {
            written = await _clipboard.WriteTextAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalException)
        {
            return false;
        }

        if (!written)
        {
            return false;
        }

        lock (_stateLock)
        {
            // 写入期间若有更新版本完成，不能把游标退回旧值
            if (version <= _lastServerVersion)
            {
                Logger.Log($"Apply: version {version} completed but cursor already advanced to {_lastServerVersion}; not reporting version.");
                return true;
            }
            _lastServerVersion = version;
            _lastRemoteAppliedUtc = _timeProvider.GetUtcNow();
        }
        _onServerVersionAdvanced?.Invoke(version);
        _onRemoteTextApplied(text);
        return true;
    }

    private async Task RetryApplyAsync(ulong version, string text, string hashHex, CancellationToken cancellationToken)
    {
        var delays = ApplyRetryDelays;
        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

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

            // 暂停期间不得继续改写剪贴板：暂停语义是"完全不碰本地剪贴板"
            if (IsPaused)
            {
                Logger.Log($"RetryApplyAsync: paused; abandoning retry for version {version}.");
                return;
            }

            lock (_stateLock)
            {
                if (_lastServerVersion > version)
                {
                    Logger.Log($"RetryApplyAsync: version {version} expired (current cursor {_lastServerVersion}), abandoning retry.");
                    return;
                }
            }

            Logger.Log($"RetryApplyAsync: attempt {attempt + 1}/{delays.Length} for version {version}.");
            if (await TryApplyOnceAsync(version, text, hashHex, cancellationToken).ConfigureAwait(false))
            {
                Logger.Log($"RetryApplyAsync: attempt {attempt + 1} succeeded for version {version}.");
                return;
            }
            Logger.Log($"RetryApplyAsync: attempt {attempt + 1} failed for version {version}.");
        }

        Logger.Log($"RetryApplyAsync: all {delays.Length} retries exhausted for version {version}.");
        _onStatus(CoreStatus.Pack(ErrorCodes.ClipboardWriteFailed, "Clipboard remains locked after background retries."));
    }

    // 重连后补发断线期间丢弃的本地内容。
    // 历史实现是"把内容写回剪贴板，指望 ClipboardMonitor 再捕获一次"——这条链是断的：
    // ClipboardMonitor 读取时就把 hash 记住了，写回后读到同样内容会被去重直接拦掉，
    // 于是内容永久丢失，且日志不显示任何异常。
    // 现在直接走发送路径，并清掉一次阻挡它的去重状态。
    internal async Task TryResendPendingAsync(ISyncTransportSender sender)
    {
        PendingLocal? pending;
        lock (_stateLock)
        {
            pending = _pendingLocal;
            if (pending is null)
            {
                return;
            }

            if (_lastRemoteAppliedUtc > pending.StoredUtc)
            {
                Logger.Log("TryResendPendingAsync: pending local copy superseded by newer remote content, discarding.");
                _pendingLocal = null;
                return;
            }

            // 该内容曾被判定与最近远端内容相同而跳过；补发时清掉这道闸，
            // 否则重发仍会被哈希去重静默拦下。
            _lastRemoteHashHex = null;
            _pendingLocal = null;
        }

        Logger.Log("TryResendPendingAsync: resending pending local copy directly.");
        await SendLocalTextAsync(pending.Text, pending.Source, sender, CancellationToken.None).ConfigureAwait(false);
    }

    // 推进服务端版本游标（仅前进），并通过回调通知 App 层持久化
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

    // 入站明文限额：服务端不会发来超限内容，这里挡的是配置不一致或解密后膨胀的异常情况
    private bool IsWithinInboundLimits(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        var localLimit = _config.LocalMaxClipboardBytes > 0 ? _config.LocalMaxClipboardBytes : _config.MaxTextBytes;
        if (bytes > _config.MaxTextBytes || bytes > localLimit)
        {
            _onStatus(CoreStatus.Pack(ErrorCodes.ClipboardTooLarge, ErrorCodes.DirectionInbound, bytes));
            return false;
        }
        return true;
    }

    // 出站限额：按最终要发送的内容校验，口径与服务端一致。
    //   1) payload 字段：服务端对 payload 字符串做 UTF-8 字节上限校验（加密后是密文 JSON 字符串）
    //   2) 整帧：服务端另有帧上限，差额留给协议头；登录响应不含该字段，按默认配置取 64 KiB 余量
    // 任一项超限都提示一次（同一内容由监听器 hash 去重保证不重复提示）。
    private bool IsWithinOutboundLimits(string payload, string json)
    {
        var payloadBytes = Encoding.UTF8.GetByteCount(payload);
        var localLimit = _config.LocalMaxClipboardBytes > 0 ? _config.LocalMaxClipboardBytes : _config.MaxTextBytes;
        if (payloadBytes > _config.MaxTextBytes || payloadBytes > localLimit)
        {
            _onStatus(CoreStatus.Pack(ErrorCodes.ClipboardTooLarge, ErrorCodes.DirectionOutbound, payloadBytes));
            return false;
        }

        var frameBytes = Encoding.UTF8.GetByteCount(json);
        var frameLimit = Math.Min(_config.MaxTextBytes + FrameOverheadBytes, SyncClient.MaxWebSocketMessageBytes);
        if (frameBytes > frameLimit)
        {
            _onStatus(CoreStatus.Pack(ErrorCodes.ClipboardTooLarge, ErrorCodes.DirectionOutbound, frameBytes));
            return false;
        }
        return true;
    }
}



