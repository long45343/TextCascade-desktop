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
    private bool _running;
    private bool _disposed;

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
        _lastSequenceNumber = GetSequenceNumber();
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
            _lastSequenceNumber = GetSequenceNumber();
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
        _lastSequenceNumber = seq;
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
        if (!_running)
        {
            return;
        }

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
                    return;
                }
                text = Clipboard.GetText(TextDataFormat.UnicodeText);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            // 双重去重：hash + length。FNV 理论上可能碰撞，
            // 加上 length 进一步降低误判概率
            var hash = HashUtil.Fnv1A64(text);
            if (_lastContentHash == hash && _lastContentLength == text.Length)
            {
                return;
            }
            _lastContentHash = hash;
            _lastContentLength = text.Length;
            _onClipboardChanged(text);
        }
        catch
        {
            // 剪贴板可能被其他进程短暂锁定（OpenClipboard 失败），
            // 忽略本次读取即可，等待下一次序号变化。
        }
    }
}