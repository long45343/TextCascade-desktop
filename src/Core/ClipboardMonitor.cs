using System.Windows.Forms;

namespace TextCascadeSharp.Core;

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
        CreateHandle(new CreateParams());
        NativeMethods.AddClipboardFormatListener(Handle);
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
            // Clipboard can be temporarily locked by another process.
        }
    }
}
