using System.Text;

namespace TextCascadeSharp.Core;

// 协议消息的领域语义：welcome/clip/clip_ack/error 的应用、去重、回环抑制、
// 发送暂停、加解密衔接、大小限制与本地发送。Engine 只负责连接生命周期与
// 协议回调分发，剪贴板读写经 ClipboardBridge 完成。
public sealed class SyncSession
{
    // rate_limited 错误后的本地发送暂停时长
    private static readonly TimeSpan RateLimitPause = TimeSpan.FromSeconds(1);

    private readonly ClipConfig _config;
    private readonly ClipboardBridge _clipboard;
    private readonly Action<string> _onStatus;
    private readonly Action<string> _onRemoteTextApplied;
    private readonly Action<ulong>? _onServerVersionAdvanced;
    private readonly TimeProvider _timeProvider;
    private readonly object _stateLock = new();
    // 发送通道：Engine 每次建连成功后设为当前 client 的 SendClipAsync
    private Func<OutboundClipMessage, CancellationToken, Task>? _sendClipAsync;

    private ulong _lastServerVersion;
    private string? _lastSentHashHex;
    private string? _lastRemoteHashHex;
    private bool _suppressNextLocal;
    private bool _connected;
    // rate_limited 暂停发送截止时刻（_timeProvider 时间）
    private DateTimeOffset _sendPausedUntil = DateTimeOffset.MinValue;
    // 本地剪贴板最后变更时刻（snapshot.localModifiedAtUtc 用）
    private DateTimeOffset _lastLocalChangeUtc;

    public SyncSession(
        ClipConfig config,
        ClipboardBridge clipboard,
        Action<string> onStatus,
        Action<string> onRemoteTextApplied,
        Action<ulong>? onServerVersionAdvanced = null,
        TimeProvider? timeProvider = null)
    {
        _config = config;
        _clipboard = clipboard;
        _onStatus = onStatus;
        _onRemoteTextApplied = onRemoteTextApplied;
        _onServerVersionAdvanced = onServerVersionAdvanced;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastLocalChangeUtc = _timeProvider.GetUtcNow();
        _lastServerVersion = config.LastServerVersion;
    }

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

    // 发送通道：Engine 每次建连成功后设为当前 client 的 SendClipAsync
    public Func<OutboundClipMessage, CancellationToken, Task>? SendClipAsync
    {
        set => _sendClipAsync = value;
    }

    // 切换本地发送门（连接状态）
    public void SetConnected(bool connected)
    {
        lock (_stateLock)
        {
            _connected = connected;
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

    // 构建 hello 快照：读取本地剪贴板，非空且未超限时加密并记录本端 hash（防回环）
    public async Task<HelloSnapshot?> BuildHelloSnapshotAsync()
    {
        HelloSnapshot? snapshot = null;
        var text = await _clipboard.ReadTextAsync(CancellationToken.None).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(text) && IsWithinLimits(text, ErrorCodes.DirectionOutbound))
        {
            var hashHex = HashUtil.Fnv1A64Hex(text);
            var payload = text;
            var encrypted = false;
            if (_config.CipherEnabled && _config.DerivedKeyBase64.Length > 0)
            {
                payload = JsonUtil.EncryptedPayload(CryptoManager.Encrypt(text, _config.DerivedKeyBase64));
                encrypted = true;
            }
            DateTimeOffset localChangeUtc;
            lock (_stateLock)
            {
                localChangeUtc = _lastLocalChangeUtc;
            }
            snapshot = new HelloSnapshot(payload, encrypted, hashHex, JsonUtil.Rfc3339Utc(localChangeUtc.UtcDateTime));
            // 快照视为本端当前值：记录 hash 防止服务端广播回环
            lock (_stateLock)
            {
                _lastSentHashHex = hashHex;
            }
        }
        return snapshot;
    }

    // 处理本地剪贴板新内容：抑制/连接/暂停/大小/hash 去重/加密/帧大小自检/发送
    public async Task SendLocalTextAsync(string text, string source, CancellationToken cancellationToken)
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
                _onStatus(CoreStatus.Pack(ErrorCodes.IgnoredNotConnected, source));
                return;
            }
            if (_timeProvider.GetUtcNow() < _sendPausedUntil)
            {
                // rate_limited 后的本地暂停窗口
                return;
            }
        }

        if (!IsWithinLimits(text, ErrorCodes.DirectionOutbound))
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
            _onStatus(CoreStatus.Pack(ErrorCodes.ClipboardTooLarge, ErrorCodes.DirectionOutbound, Encoding.UTF8.GetByteCount(json)));
            return;
        }

        var sendClipAsync = _sendClipAsync;
        if (sendClipAsync is null)
        {
            return;
        }

        try
        {
            await sendClipAsync(clip, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常关停：不再抛出，避免火忘任务产生未观察异常
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

            if (!IsWithinLimits(text, ErrorCodes.DirectionInbound))
            {
                return;
            }

            var written = await _clipboard.TryWriteTextAsync(text, CancellationToken.None).ConfigureAwait(false);
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
                _onStatus(CoreStatus.Pack(ErrorCodes.ClipboardWriteFailed, "Clipboard remains locked."));
            }
        }
        catch (Exception error)
        {
            _onStatus(CoreStatus.Pack(ErrorCodes.InboundError, error.Message));
        }
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

    // 检查内容字节数是否在服务端和本地限制内
    private bool IsWithinLimits(string text, string direction)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        var localLimit = _config.LocalMaxClipboardBytes > 0 ? _config.LocalMaxClipboardBytes : _config.MaxTextBytes;
        var ok = bytes <= _config.MaxTextBytes && bytes <= localLimit;
        if (!ok)
        {
            _onStatus(CoreStatus.Pack(ErrorCodes.ClipboardTooLarge, direction, bytes));
        }
        return ok;
    }
}