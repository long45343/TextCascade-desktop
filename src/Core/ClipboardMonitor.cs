using System.Windows.Forms;

namespace TextCascadeSharp.Core;

// 监听系统剪贴板变化并回调。
// 三重保险：
//   1) AddClipboardFormatListener：实时接收 WM_CLIPBOARDUPDATE 消息
//   2) 2 秒低开销轮询 Timer：仅比对 GetClipboardSequenceNumber 序列号，
//      序号未变时不读取剪贴板文本、不计算哈希，空闲期完全不碰剪贴板锁
//   3) 读取遇剪贴板独占（ExternalException）时不消费序号，当场短重试数次，
//      全失败仍保留序号由下一轮轮询兜底，避免单次竞争导致内容永久丢失
// 本地用 FNV hash 去重，避免对相同内容反复回调。
// 可选防抖延时（UU 远程等场景）：变化后延迟读取，快速连续复制只广播最新一条。
public sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly Action<string> _onClipboardChanged;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _delayTimer;
    private readonly Func<uint>? _getSequenceNumberOverride;
    private readonly Func<string?>? _getTextOverride;
    private readonly Func<TimeSpan>? _getReadDelay;
    private uint _lastSequenceNumber;
    private ulong? _lastContentHash;
    private int _lastContentLength;
    // 0=无重试在途，1=有（Interlocked 单飞，避免多次失败叠加出多串重试）
    private int _retryPending;
    private bool _running;
    private bool _disposed;

    internal int RetryAttempts = 3;
    internal TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public ClipboardMonitor(Action<string> onClipboardChanged, Func<TimeSpan>? getReadDelay = null)
        : this(onClipboardChanged, null, null, getReadDelay)
    {
    }

    internal ClipboardMonitor(
        Action<string> onClipboardChanged,
        Func<uint>? getSequenceNumberOverride,
        Func<string?>? getTextOverride,
        Func<TimeSpan>? getReadDelay = null)
    {
        _onClipboardChanged = onClipboardChanged;
        _getSequenceNumberOverride = getSequenceNumberOverride;
        _getTextOverride = getTextOverride;
        _getReadDelay = getReadDelay;
        // 创建一个隐形消息窗口用于接收 Windows 消息
        CreateHandle(new CreateParams());
        NativeMethods.AddClipboardFormatListener(Handle);
        // 2 秒轮询：仅比对序列号，不读取剪贴板文本或计算哈希
        _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _pollTimer.Tick += (_, _) => OnPollTick();
        // 防抖延迟读取：到期才真正读取，期间再次变化会重置倒计时
        _delayTimer = new System.Windows.Forms.Timer();
        _delayTimer.Tick += (_, _) =>
        {
            _delayTimer.Stop();
            ReadAndNotifyInternal(fromRetry: false);
        };
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }
        _running = true;
        _pollTimer.Start();
        ReadAndNotify();
    }

    public void Stop()
    {
        _running = false;
        _pollTimer.Stop();
        _delayTimer.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
        // 必须取消监听，否则系统会继续向已销毁的窗口发消息
        NativeMethods.RemoveClipboardFormatListener(Handle);
        DestroyHandle();
        _pollTimer.Dispose();
        _delayTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate)
        {
            ReadAndNotify();
        }
        base.WndProc(ref m);
    }

    internal void OnPollTick()
    {
        if (!_running)
        {
            return;
        }
        var seq = GetSequenceNumber();
        if (seq == _lastSequenceNumber)
        {
            return;
        }
        ReadAndNotify();
    }

    private uint GetSequenceNumber()
    {
        return _getSequenceNumberOverride is not null
            ? _getSequenceNumberOverride()
            : NativeMethods.GetClipboardSequenceNumber();
    }

    // 测试接缝：是否有防抖延时在倒计时
    internal bool IsDelayPending => _delayTimer.Enabled;

    // 测试接缝：立即触发一次防抖到期读取
    internal void TriggerDelayTickForTest()
    {
        _delayTimer.Stop();
        ReadAndNotifyInternal(fromRetry: false);
    }

    // 入口：先过防抖判定，需要延时则重置倒计时后返回
    private void ReadAndNotify()
    {
        if (!_running)
        {
            return;
        }

        var delay = _getReadDelay?.Invoke() ?? TimeSpan.Zero;
        if (delay > TimeSpan.Zero)
        {
            // 防抖延迟：重置并启动定时器，延时到期后再读取
            _delayTimer.Interval = Math.Max(1, (int)delay.TotalMilliseconds);
            _delayTimer.Stop();
            _delayTimer.Start();
            return;
        }

        _delayTimer.Stop();
        ReadAndNotifyInternal(fromRetry: false);
    }

    // 读取遇独占后的短重试。重试路径不再经过防抖判定，否则每次重试都会被再延一次
    private void TriggerRetryRead()
    {
        if (Interlocked.CompareExchange(ref _retryPending, 1, 0) != 0)
        {
            return;
        }

        _ = RetryReadAsync();
    }

    private async Task RetryReadAsync()
    {
        try
        {
            for (var i = 0; i < RetryAttempts; i++)
            {
                if (!_running)
                {
                    break;
                }

                if (RetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(RetryDelay).ConfigureAwait(true);
                }

                if (!_running)
                {
                    break;
                }

                var seqBefore = GetSequenceNumber();
                ReadAndNotifyInternal(fromRetry: true);
                // 序号已被成功消费说明本次读取完成（内容已处理或确认无文本）
                if (_lastSequenceNumber == seqBefore)
                {
                    break;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _retryPending, 0);
        }
    }

    // 真正读取剪贴板并按需回调。
    // 序号消费规则：所有正常路径都消费；仅 ExternalException 且非重试路径不消费，
    // 以便交给短重试 / 下一轮轮询重新处理同一份内容。
    private void ReadAndNotifyInternal(bool fromRetry)
    {
        if (!_running)
        {
            return;
        }

        var seq = GetSequenceNumber();

        try
        {
            string? text;
            if (_getTextOverride is not null)
            {
                text = _getTextOverride();
            }
            else
            {
                if (!Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    _lastSequenceNumber = seq;
                    return;
                }
                text = Clipboard.GetText(TextDataFormat.UnicodeText);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                _lastSequenceNumber = seq;
                return;
            }
            // 双重去重：hash + length。FNV 理论上可能碰撞，
            // 加上 length 进一步降低误判概率
            var hash = HashUtil.Fnv1A64(text);
            if (_lastContentHash == hash && _lastContentLength == text.Length)
            {
                _lastSequenceNumber = seq;
                return;
            }
            _lastContentHash = hash;
            _lastContentLength = text.Length;
            _lastSequenceNumber = seq;
            _onClipboardChanged(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // 剪贴板被其他进程独占（CLIPBRD_E_CANT_OPEN 等）：不消费序号，
            // 当场短重试；重试路径失败则留给 2 秒轮询兜底。
            if (!fromRetry)
            {
                TriggerRetryRead();
            }
        }
        catch (Exception error)
        {
            // 非预期异常：消费序号避免每轮重复触发同一故障，同时记录堆栈
            _lastSequenceNumber = seq;
            Logger.LogError("Unexpected error in ClipboardMonitor.ReadAndNotify", error);
        }
    }
}
