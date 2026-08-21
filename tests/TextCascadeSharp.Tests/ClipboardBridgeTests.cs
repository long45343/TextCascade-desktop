using System.Runtime.InteropServices;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

// 剪贴板读写桥：重试、异常转发、fake 注入。
// cmd 兜底依赖真实系统剪贴板与 cmd.exe，不做确定性断言，仅在此覆盖可确定的路径。
public class ClipboardBridgeTests
{
    private static readonly TestSynchronizationContext UiContext = new();

    [Fact]
    public async Task TryWriteText_SucceedsFirstAttempt_UsesOverride()
    {
        var calls = 0;
        var bridge = new ClipboardBridge(
            UiContext,
            setOverride: (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            },
            getOverride: () => string.Empty);

        var written = await bridge.TryWriteTextAsync("hello", CancellationToken.None);

        Assert.True(written);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TryWriteText_OverrideThrowsNonClipboard_FaultsThroughToCaller()
    {
        var bridge = new ClipboardBridge(
            UiContext,
            setOverride: (_, _) => throw new InvalidOperationException("boom"),
            getOverride: () => string.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bridge.TryWriteTextAsync("hello", CancellationToken.None));
    }

    [Fact]
    public async Task ReadText_UsesGetOverride()
    {
        var bridge = new ClipboardBridge(UiContext, getOverride: () => "clip");

        var text = await bridge.ReadTextAsync(CancellationToken.None);

        Assert.Equal("clip", text);
    }

    [Fact]
    public async Task SetClipboardWithRetry_SucceedsFirstAttempt()
    {
        var calls = 0;
        var ok = await ClipboardBridge.SetClipboardWithRetryAsync("t", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        Assert.True(ok);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SetClipboardWithRetry_AllAttemptsFail_ReturnsFalse()
    {
        var calls = 0;
        var ok = await ClipboardBridge.SetClipboardWithRetryAsync("t", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new ExternalException("locked");
        }, maxAttempts: 3, retryDelay: TimeSpan.FromMilliseconds(10));

        Assert.False(ok);
        Assert.Equal(3, calls);
    }
}
