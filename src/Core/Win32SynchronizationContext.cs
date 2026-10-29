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
