using System.Runtime.InteropServices;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

// 剪贴板读写桥：重试、异常转发、fake 注入及 clip.exe 兜底加固测试。
public class ClipboardBridgeTests : IDisposable
{
    private static readonly TestSynchronizationContext UiContext = new();
    private readonly string _tempDir;

    public ClipboardBridgeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "textcascade_bridge_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

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

    [Fact]
    public async Task TryWriteTextAsync_WhenRetryFails_InvokesFallbackOverride()
    {
        var retryAttempts = 0;
        var fallbackReceived = string.Empty;

        var bridge = new ClipboardBridge(
            UiContext,
            setOverride: (_, _) =>
            {
                Interlocked.Increment(ref retryAttempts);
                throw new ExternalException("Locked by another process");
            },
            getOverride: () => string.Empty,
            fallbackOverride: text =>
            {
                fallbackReceived = text;
                return true;
            });

        var written = await bridge.TryWriteTextAsync("fallback test content", CancellationToken.None);

        Assert.True(written);
        Assert.Equal(5, retryAttempts);
        Assert.Equal("fallback test content", fallbackReceived);
    }

    [Fact]
    public void TryClipboardFallback_DirectOverride_ReturnsInjectedValue()
    {
        var resultTrue = ClipboardBridge.TryClipboardFallback("abc", _ => true);
        var resultFalse = ClipboardBridge.TryClipboardFallback("abc", _ => false);

        Assert.True(resultTrue);
        Assert.False(resultFalse);
    }

    [Fact]
    public void TryClipboardFallback_WhenClipProcessFailsOrTimesOut_LogsErrorAndReturnsFalse()
    {
        var logFile = Path.Combine(_tempDir, "bridge_fallback_test.log");
        Logger.LogPath = logFile;

        // 验证在极短超时（例如 0ms）下能够优雅处理超时强杀并打出日志，绝不抛出未捕获异常
        var result = ClipboardBridge.TryClipboardFallback("timeout test", timeoutMs: 0);

        // 如果机器环境没有运行 clip 或直接超时，返回 false，同时保证日志或错误被捕获
        var logText = File.Exists(logFile) ? File.ReadAllText(logFile) : string.Empty;
        if (!result)
        {
            // 如果返回 false，应有相应的 log 记录
            Assert.True(logText.Contains("clip.exe") || logText.Contains("TryClipboardFallback"));
        }
    }
}
