using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TextCascadeSharp.Core;

// 剪贴板读写桥：统一在 UI 线程执行，处理短退避重试与 clip.exe 兜底。
// Engine 不再直接持有剪贴板访问权限，只通过本桥读写，便于单元测试注入 fake。
public sealed class ClipboardBridge
{
    private readonly SynchronizationContext _uiContext;
    private readonly Func<string, CancellationToken, Task>? _setOverride;
    private readonly Func<string>? _getOverride;
    private readonly Func<string, bool>? _fallbackOverride;

    public ClipboardBridge(
        SynchronizationContext uiContext,
        Func<string, CancellationToken, Task>? setOverride = null,
        Func<string>? getOverride = null,
        Func<string, bool>? fallbackOverride = null)
    {
        _uiContext = uiContext;
        _setOverride = setOverride;
        _getOverride = getOverride;
        _fallbackOverride = fallbackOverride;
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

    // 写剪贴板：5×100ms 短退避重试，仍失败再走 clip.exe 兜底。返回是否成功。
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
                // 独占锁耗尽重试后，调用系统 clip.exe 或注入的 fallback 处理
                written = TryClipboardFallback(text, _fallbackOverride);
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
                // 最后一轮仍失败：把结果交回调用方决定是否走 clip.exe 兜底
                return false;
            }
        }
    }

    // 系统兜底：绝对定位系统目录 clip.exe，以 Unicode 编码从标准输入写入文本，具备 500ms 超时强杀与错误日志记录。
    internal static bool TryClipboardFallback(
        string text,
        Func<string, bool>? fallbackOverride = null,
        int timeoutMs = 500)
    {
        if (fallbackOverride is not null)
        {
            return fallbackOverride(text);
        }

        try
        {
            var clipPath = Path.Combine(Environment.SystemDirectory, "clip.exe");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(clipPath)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = Encoding.Unicode
                }
            };
            if (!process.Start())
            {
                Logger.LogError("Failed to start clip.exe process for clipboard fallback.");
                return false;
            }
            process.StandardInput.Write(text);
            process.StandardInput.Close();
            if (!process.WaitForExit(timeoutMs))
            {
                Logger.LogError($"clip.exe timed out after {timeoutMs}ms; killing process tree.");
                process.Kill(entireProcessTree: true);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception error)
        {
            Logger.LogError("Unexpected error in TryClipboardFallback", error);
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
