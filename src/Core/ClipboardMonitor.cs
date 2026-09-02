using System.Windows.Forms;

namespace TextCascadeSharp.Core;

// 监听系统剪贴板变化并回调。
// 双重保险：
//   1) AddClipboardFormatListener：实时接收 WM_CLIPBOARDUPDATE 消息
//   2) 2 秒低开销轮询 Timer：仅比对 GetClipboardSequenceNumber 序列号，
//      序号变化时才触发读取，空闲时不读取剪贴板文本或计算哈希
// 本地用 FNV hash 去重，避免对相同内容反复回调。
public sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly Action<string> _onClipboardChanged;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly Func<uint>? _getSequenceNumberOverride;
    private readonly Func<string?>? _getTextOverride;
    private uint _lastSequenceNumber;
    private ulong? _lastContentHash;
    private int _lastContentLength;
    private int _retryPending;
    private bool _running;
    private bool _disposed;

    internal int RetryAttempts = 3;
    internal TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public ClipboardMonitor(Action<string> onClipboardChanged)
        : this(onClipboardChanged, null, null)
    {
    }

    internal ClipboardMonitor(
        Action<string> onClipboardChanged,
        Func<uint>? getSequenceNumberOverride,
        Func<string?>? getTextOverride)
    {
        _onClipboardChanged = onClipboardChanged;
        _getSequenceNumberOverride = getSequenceNumberOverride;
        _getTextOverride = getTextOverride;
        // 创建一个隐形消息窗口用于接收 Windows 消息
        CreateHandle(new CreateParams());
        NativeMethods.AddClipboardFormatListener(Handle);
        // 2 秒轮询：仅比对序列号，不读取剪贴板文本或计算哈希
        _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _pollTimer.Tick += (_, _) => OnPollTick();
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

    private void ReadAndNotify()
    {
        ReadAndNotifyInternal(fromRetry: false);
    }

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
                    // Clipboard 仅允许 STA(UI)线程访问，重试必须回流 UI 上下文
                    await Task.Delay(RetryDelay);
                }

                if (!_running)
                {
                    break;
                }

                var seqBefore = GetSequenceNumber();
                ReadAndNotifyInternal(fromRetry: true);
                // 若序号已被成功消费（或者内容已变并处理完毕），则重试成功退出
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
            if (!fromRetry)
            {
                TriggerRetryRead();
            }
        }
        catch (Exception error)
        {
            _lastSequenceNumber = seq;
            Logger.LogError("Unexpected error in ClipboardMonitor.ReadAndNotify", error);
        }
    }
}
