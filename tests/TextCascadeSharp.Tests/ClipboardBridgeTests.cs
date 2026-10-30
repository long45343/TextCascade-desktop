using System.Runtime.InteropServices;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

// 剪贴板读写桥：只负责把一次读写封送到 UI 线程。
// 写入重试已收归 SyncSession（它同时掌握版本与暂停状态），因此这里只验证
// 成功/失败/异常/读取四条路径，不再测试重试次数。
public class ClipboardBridgeTests
{
    private static readonly TestSynchronizationContext UiContext = new();

    [Fact]
    public async Task WriteText_Succeeds_UsesOverride()
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

        var written = await bridge.WriteTextAsync("hello", CancellationToken.None);

        Assert.True(written);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task WriteText_DoesNotRetry_AttemptsExactlyOnce()
    {
        var calls = 0;
        var bridge = new ClipboardBridge(
            UiContext,
            setOverride: (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new ExternalException("Locked by another process");
            },
            getOverride: () => string.Empty);

        await Assert.ThrowsAsync<ExternalException>(
            () => bridge.WriteTextAsync("hello", CancellationToken.None));

        // 桥不再自带重试：重试由 SyncSession 负责
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task WriteText_OverrideThrowsNonClipboard_FaultsThroughToCaller()
    {
        var bridge = new ClipboardBridge(
            UiContext,
            setOverride: (_, _) => throw new InvalidOperationException("boom"),
            getOverride: () => string.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bridge.WriteTextAsync("hello", CancellationToken.None));
    }

    [Fact]
    public async Task ReadText_UsesGetOverride()
    {
        var bridge = new ClipboardBridge(UiContext, getOverride: () => "clip");

        var text = await bridge.ReadTextAsync(CancellationToken.None);

        Assert.Equal("clip", text);
    }
}
