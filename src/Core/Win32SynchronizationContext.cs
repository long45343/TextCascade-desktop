using System.Collections.Concurrent;

namespace TextCascadeSharp.Core;

// 专为原生 Win32 消息循环构建的极轻量 SynchronizationContext。
// 彻底脱离 Windows Forms，通过向主线程宿主 HWND 投递 WM_APP_DISPATCH 消息，
// 将后台异步任务（Task 延续/回调）安全排队派发回 STA 主线程执行，保证剪贴板读写的线程安全性。
public sealed class Win32SynchronizationContext : SynchronizationContext
{
    private readonly IntPtr _hwnd;
    private readonly int _mainThreadId;
    private readonly ConcurrentQueue<Action> _queue = new();

    public Win32SynchronizationContext(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _mainThreadId = Environment.CurrentManagedThreadId;
    }

    // 宿主 UI 线程 ID：承载消息泵，也是所有 Win32 子窗口的合法归属线程。
    // 需要在别的线程上判断"当前是否在 UI 线程"时用它。
    public int MainThreadId => _mainThreadId;

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue(() => d(state));
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_APP_DISPATCH, 0, 0);
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == _mainThreadId)
        {
            d(state);
            return;
        }

        using var mres = new ManualResetEventSlim(false);
        Exception? caught = null;
        _queue.Enqueue(() =>
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                mres.Set();
            }
        });

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_APP_DISPATCH, 0, 0);
        }
        mres.Wait();
        if (caught is not null)
        {
            throw new AggregateException(caught);
        }
    }

    // 由宿主窗口在主 STA 线程接收到 WM_APP_DISPATCH 消息时调用，按先进先出排队消费
    public void ProcessQueue()
    {
        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.LogError("Exception in Win32SynchronizationContext dispatched action", ex);
            }
        }
    }
}
