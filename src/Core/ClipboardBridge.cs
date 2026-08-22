using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TextCascadeSharp.Core;

// 剪贴板读写桥：统一在 UI 线程执行，处理短退避重试与 cmd 兜底。
// Engine 不再直接持有剪贴板访问权限，只通过本桥读写，便于单元测试注入 fake。
public sealed class ClipboardBridge
{
    private readonly SynchronizationContext _uiContext;
    private readonly Func<string, CancellationToken, Task>? _setOverride;
    private readonly Func<string>? _getOverride;

    public ClipboardBridge(
        SynchronizationContext uiContext,
        Func<string, CancellationToken, Task>? setOverride = null,
        Func<string>? getOverride = null)
    {
        _uiContext = uiContext;
        _setOverride = setOverride;
        _getOverride = getOverride;
    }

    // 读取本地剪贴板文本（hello snapshot 用）。失败返回空串
    public async Task<string> ReadTextAsync(CancellationToken cancellationToken)
    {
        if (_getOverride is { } fake)
        {
            return await Task.Run(fake, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var text = string.Empty;
            await InvokeUiAsync(() =>
            {
                text = Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    ? Clipboard.GetText(TextDataFormat.UnicodeText)
                    : string.Empty;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            return text;
        }
        catch
        {
            // 剪贴板被占用等情况：本次不带 snapshot
            return string.Empty;
        }
    }

    // 写剪贴板：5×100ms 短退避重试，仍失败再走 cmd 兜底。返回是否成功。
    // 全程在 UI 线程执行，重试间隙让出消息循环。
    public async Task<bool> TryWriteTextAsync(string text, CancellationToken cancellationToken)
    {
        var result = false;
        await InvokeUiAsync(async () =>
        {
            var written = await SetClipboardWithRetryAsync(text, _setOverride, cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            if (!written)
            {
                // 受限环境（如 AppLocker）下 cmd 不可用时会失败，返回 false 由上层报错
                written = TryClipboardFallback(text);
            }
            result = written;
        }).ConfigureAwait(false);
        return result;
    }

    // 短退避重试：默认 5 次 × 100ms。返回是否成功；最终失败由调用方决定兜底策略
    internal static async Task<bool> SetClipboardWithRetryAsync(
        string text,
        Func<string, CancellationToken, Task>? setAsync = null,
        int maxAttempts = 5,
        TimeSpan? retryDelay = null,
        CancellationToken cancellationToken = default)
    {
        setAsync ??= static (t, _) =>
        {
            Clipboard.SetText(t, TextDataFormat.UnicodeText);
            return Task.CompletedTask;
        };
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(100);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await setAsync(text, cancellationToken).ConfigureAwait(true);
                return true;
            }
            catch (ExternalException) when (attempt < maxAttempts)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
            }
            catch (ExternalException)
            {
                // 最后一轮仍失败：把结果交回调用方决定是否走 cmd 兜底
                return false;
            }
        }
    }

    // cmd 兜底：通过 clip 从标准输入写入文本
    private static bool TryClipboardFallback(string text)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe", "/c clip")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!process.Start())
            {
                return false;
            }
            process.StandardInput.Write(text);
            process.StandardInput.Close();
            if (!process.WaitForExit(1000))
            {
                process.Kill();
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // 把需要在 UI 线程执行的操作转发过去，并返回可等待的 Task
    private Task InvokeUiAsync(Action action)
    {
        if (_uiContext == SynchronizationContext.Current)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(static state =>
        {
            var (work, completion) = ((Action, TaskCompletionSource))state!;
            try
            {
                work();
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        }, (action, tcs));
        return tcs.Task;
    }

    // 异步版 UI 转发：重试间隙让出 UI 线程，消息循环继续泵消息
    private Task InvokeUiAsync(Func<Task> action)
    {
        if (_uiContext == SynchronizationContext.Current)
        {
            return action();
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(static async state =>
        {
            var (work, completion) = ((Func<Task>, TaskCompletionSource))state!;
            try
            {
                await work().ConfigureAwait(true);
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        }, (action, tcs));
        return tcs.Task;
    }
}