using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TextCascadeSharp.Core;

// 剪贴板读写桥：统一在 UI/STA 线程执行。
// 基于纯原生 Win32 API 实现，彻底脱离 WinForms。
// Engine 不再直接持有剪贴板访问权限，只通过本桥读写，便于单元测试注入 fake。
//
// 重试策略不在这里：写入重试（短退避 + 后台重试）统一由 SyncSession 持有，
// 因为只有会话层同时掌握"当前版本号"与"是否暂停"，能把版本判定和写入放同一个临界区。
// 桥只负责一件事：把一次写入封送到 UI 线程并如实返回成功/失败。
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
                if (NativeClipboard.TryGetText(out var read))
                {
                    text = read;
                }
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

    // 写剪贴板：单次尝试，不做重试。返回是否成功。
    // 剪贴板被占用时抛 ExternalException，由调用方决定退避策略。
    public async Task<bool> WriteTextAsync(string text, CancellationToken cancellationToken)
    {
        Func<string, CancellationToken, Task> setter = _setOverride ?? WriteToNative;
        var result = false;
        await InvokeUiAsync(async () =>
        {
            await setter(text, cancellationToken).ConfigureAwait(true);
            result = true;
        }).ConfigureAwait(false);
        return result;
    }

    private static Task WriteToNative(string text, CancellationToken cancellationToken)
    {
        if (!NativeClipboard.TrySetText(text))
        {
            throw new ExternalException("Failed to set clipboard text using native API.");
        }
        return Task.CompletedTask;
    }

    // 异步版 UI 转发：让出 UI 线程，消息循环继续泵消息
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
