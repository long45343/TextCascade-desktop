using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace TextCascadeSharp.Core;

// 监听系统剪贴板变化并回调。
// 基于纯原生 Win32 消息窗口与 API 实现，彻底脱离 WinForms。
// 双重保险：
//   1) AddClipboardFormatListener：实时接收 WM_CLIPBOARDUPDATE 消息
//   2) 2 秒低开销轮询 Timer：仅比对 GetClipboardSequenceNumber 序列号，
//      序号变化时才触发读取，空闲时不读取剪贴板文本或计算哈希
// 本地用 FNV hash 去重，避免对相同内容反复回调。
public sealed class ClipboardMonitor : IDisposable
{
    private const string WindowClassName = "TextCascade_ClipMonitorWndClass";
    private const nuint TimerIdPoll = 1;
    private const nuint TimerIdDelay = 2;

    private static readonly ConcurrentDictionary<IntPtr, ClipboardMonitor> Instances = new();
    private static readonly NativeMethods.WndProc StaticWndProc = WindowProcedure;
    private static readonly object RegisterLock = new();
    private static bool _classRegistered;

    private readonly IntPtr _hwnd;
    private readonly Action<string> _onClipboardChanged;
    private readonly Func<uint>? _getSequenceNumberOverride;
    private readonly Func<string?>? _getTextOverride;
    private readonly Func<TimeSpan>? _getReadDelay;
    private uint _lastSequenceNumber;
    private ulong? _lastContentHash;
    private int _lastContentLength;
    private int _retryPending;
    private bool _running;
    private bool _disposed;
    private bool _delayTimerActive;

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

        EnsureWindowClassRegistered();

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            WindowClassName,
            "TextCascadeClipMonitor",
            0,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            Instances[_hwnd] = this;
            var ok = NativeMethods.AddClipboardFormatListener(_hwnd);
            Logger.Log($"ClipboardMonitor window created (hwnd=0x{_hwnd.ToInt64():X}), AddClipboardFormatListener={ok}, thread={Environment.CurrentManagedThreadId}.");
        }
        else
        {
            // 创建失败必须留痕：静默失败会让同步彻底停摆，而日志里看不到任何异常
            Logger.LogError("ClipboardMonitor failed to create its message window; clipboard sync is disabled.");
        }
    }

    public IntPtr Handle => _hwnd;

    public void Start()
    {
        if (_running)
        {
            return;
        }
        _running = true;
        Logger.Log($"ClipboardMonitor.Start (hwnd=0x{_hwnd.ToInt64():X}).");
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetTimer(_hwnd, TimerIdPoll, 2000, IntPtr.Zero);
        }
        ReadAndNotify();
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }
        _running = false;
        Logger.Log("ClipboardMonitor.Stop.");
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.KillTimer(_hwnd, TimerIdPoll);
            StopDelayTimer();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
            Instances.TryRemove(_hwnd, out _);
            NativeMethods.DestroyWindow(_hwnd);
        }
        GC.SuppressFinalize(this);
    }

    private static unsafe void EnsureWindowClassRegistered()
    {
        if (_classRegistered)
        {
            return;
        }
        lock (RegisterLock)
        {
            if (_classRegistered)
            {
                return;
            }

            fixed (char* pClassName = WindowClassName)
            {
                var wcx = new NativeMethods.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(NativeMethods.WNDCLASSEXW),
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticWndProc),
                    cbClsExtra = 0,
                    cbWndExtra = 0,
                    hInstance = NativeMethods.GetModuleHandle(null),
                    hIcon = IntPtr.Zero,
                    hCursor = IntPtr.Zero,
                    hbrBackground = IntPtr.Zero,
                    lpszMenuName = null,
                    lpszClassName = pClassName,
                    hIconSm = IntPtr.Zero
                };
                NativeMethods.RegisterClassEx(wcx);
            }
            _classRegistered = true;
        }
    }

    private static nint WindowProcedure(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
    {
        if (Instances.TryGetValue(hWnd, out var monitor))
        {
            return monitor.HandleMessage(hWnd, msg, wParam, lParam);
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private nint HandleMessage(IntPtr hWnd, uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_CLIPBOARDUPDATE:
                ReadAndNotify();
                return 0;

            case NativeMethods.WM_TIMER:
                if (wParam == TimerIdPoll)
                {
                    OnPollTick();
                    return 0;
                }
                if (wParam == TimerIdDelay)
                {
                    StopDelayTimer();
                    ReadAndNotifyInternal(fromRetry: false);
                    return 0;
                }
                break;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
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

    internal bool IsDelayPending => _delayTimerActive;

    internal void TriggerDelayTickForTest()
    {
        StopDelayTimer();
        ReadAndNotifyInternal(fromRetry: false);
    }

    private void StartDelayTimer(TimeSpan delay)
    {
        if (_hwnd != IntPtr.Zero)
        {
            var ms = (uint)Math.Max(1, (int)delay.TotalMilliseconds);
            NativeMethods.SetTimer(_hwnd, TimerIdDelay, ms, IntPtr.Zero);
            _delayTimerActive = true;
        }
        else
        {
            _delayTimerActive = true;
        }
    }

    private void StopDelayTimer()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.KillTimer(_hwnd, TimerIdDelay);
        }
        _delayTimerActive = false;
    }

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
            Logger.Log($"ClipboardMonitor: change deferred by {delay.TotalMilliseconds}ms debounce.");
            StopDelayTimer();
            StartDelayTimer(delay);
            return;
        }

        StopDelayTimer();
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
                    await Task.Delay(RetryDelay);
                }

                if (!_running)
                {
                    break;
                }

                var seqBefore = GetSequenceNumber();
                ReadAndNotifyInternal(fromRetry: true);
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
                if (!NativeClipboard.TryGetText(out var readText))
                {
                    throw new ExternalException("Clipboard is locked by another process.");
                }
                text = readText;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                _lastSequenceNumber = seq;
                return;
            }

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
        catch (ExternalException)
        {
            if (!fromRetry)
            {
                TriggerRetryRead();
            }
        }
        catch (Exception error)
        {
            Logger.LogError("Unexpected error in ClipboardMonitor.ReadAndNotify", error);
        }
    }
}
