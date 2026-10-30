using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace TextCascadeSharp.Core;

// 监听系统剪贴板变化并回调。
// 基于纯原生 Win32 消息窗口与 API 实现，彻底脱离 WinForms。
// 只依赖 AddClipboardFormatListener + WM_CLIPBOARDUPDATE：
// 此前额外跑的 2 秒序列号轮询会与 UU 规避防抖互相重置（延时 > 2s 时防抖定时器永不到期），
// 更严重的是它会掩盖监听器本身的故障——窗口建在错误线程上收不到通知时，
// 轮询让症状退化成"偶发失效"而不是"完全不动"，反而难以发现。
// 本地用 FNV hash 去重，避免对相同内容反复回调。
public sealed class ClipboardMonitor : IDisposable
{
    private const string WindowClassName = "TextCascade_ClipMonitorWndClass";
    private const nuint TimerIdDelay = 2;

    private static readonly ConcurrentDictionary<IntPtr, ClipboardMonitor> Instances = new();
    private static readonly NativeMethods.WndProc StaticWndProc = WindowProcedure;
    private static readonly object RegisterLock = new();
    private static bool _classRegistered;

    private readonly IntPtr _hwnd;
    private readonly Action<string> _onClipboardChanged;
    private readonly Func<string?>? _getTextOverride;
    private readonly Func<TimeSpan>? _getReadDelay;
    private readonly Action<string>? _onListenerUnavailable;
    // 监听器是否已成功注册；注册失败时同步根本无法工作，必须让宿主提示用户
    private readonly bool _listenerRegistered;
    private ulong? _lastContentHash;
    private int _lastContentLength;
    private int _retryPending;
    private bool _running;
    private bool _disposed;
    private bool _delayTimerActive;

    internal int RetryAttempts = 3;
    internal TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public ClipboardMonitor(Action<string> onClipboardChanged, Func<TimeSpan>? getReadDelay = null, Action<string>? onListenerUnavailable = null)
        : this(onClipboardChanged, null, getReadDelay, onListenerUnavailable)
    {
    }

    internal ClipboardMonitor(
        Action<string> onClipboardChanged,
        Func<string?>? getTextOverride,
        Func<TimeSpan>? getReadDelay = null,
        Action<string>? onListenerUnavailable = null)
    {
        _onClipboardChanged = onClipboardChanged;
        _getTextOverride = getTextOverride;
        _getReadDelay = getReadDelay;
        _onListenerUnavailable = onListenerUnavailable;

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
            _listenerRegistered = NativeMethods.AddClipboardFormatListener(_hwnd);
            Logger.Log($"ClipboardMonitor window created (hwnd=0x{_hwnd.ToInt64():X}), AddClipboardFormatListener={_listenerRegistered}, thread={Environment.CurrentManagedThreadId}.");
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

        // 监听未注册成功时同步不可能收到任何变更：明确告知宿主，不假装在运行
        if (_hwnd == IntPtr.Zero || !_listenerRegistered)
        {
            _onListenerUnavailable?.Invoke(ErrorCodes.ClipboardListenerUnavailable);
            return;
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

    internal bool IsDelayPending => _delayTimerActive;

    // 测试钩子：模拟一次 WM_CLIPBOARDUPDATE 到达，走与真实消息完全相同的处理路径
    internal void NotifyClipboardUpdateForTest() => ReadAndNotify();

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

                if (ReadAndNotifyInternal(fromRetry: true))
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

    // 返回是否已成功读到并处理（含"内容为空/重复已被去重消费"的情况）。
    // 返回 false 表示剪贴板仍被占用，需要继续重试。
    private bool ReadAndNotifyInternal(bool fromRetry)
    {
        if (!_running)
        {
            return true;
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
                if (!NativeClipboard.TryGetText(out var readText))
                {
                    throw new ExternalException("Clipboard is locked by another process.");
                }
                text = readText;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            var hash = HashUtil.Fnv1A64(text);
            if (_lastContentHash == hash && _lastContentLength == text.Length)
            {
                return true;
            }

            _lastContentHash = hash;
            _lastContentLength = text.Length;
            _onClipboardChanged(text);
            return true;
        }
        catch (ExternalException)
        {
            if (!fromRetry)
            {
                TriggerRetryRead();
            }
            return false;
        }
        catch (Exception error)
        {
            Logger.LogError("Unexpected error in ClipboardMonitor.ReadAndNotify", error);
            return true;
        }
    }
}
