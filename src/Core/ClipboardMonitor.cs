using System.Windows.Forms;

namespace TextCascadeSharp.Core;

// 监听系统剪贴板变化并回调。
// 双重保险：
//   1) AddClipboardFormatListener：实时接收 WM_CLIPBOARDUPDATE 消息
//   2) 2 秒轮询 Timer：防止某些应用（如部分远程桌面）不触发通知
// 本地用 FNV hash 去重，避免对相同内容反复回调。
public sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly Action<string> _onClipboardChanged;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private ulong? _lastContentHash;
    private int _lastContentLength;
    private bool _running;
    private bool _disposed;

    public ClipboardMonitor(Action<string> onClipboardChanged)
    {
        _onClipboardChanged = onClipboardChanged;
        // 创建一个隐形消息窗口用于接收 Windows 消息
        CreateHandle(new CreateParams());
        NativeMethods.AddClipboardFormatListener(Handle);
        // 2 秒轮询：兼容部分不发送 WM_CLIPBOARDUPDATE 的场景
        _pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _pollTimer.Tick += (_, _) => ReadAndNotify();
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

    private void ReadAndNotify()
    {
        if (!_running)
        {
            return;
        }

        try
        {
            if (!Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                return;
            }
            var text = Clipboard.GetText(TextDataFormat.UnicodeText);
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
            // 忽略本次读取即可，下一轮 Timer 会重试。
        }
    }
}
