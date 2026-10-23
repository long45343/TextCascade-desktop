using System.Text;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

// 入站规避/延时/后台重试的行为测试。
public class InboundEvasionTests
{
    private static ClipConfig TestConfig(Func<TimeSpan>? evasionDelay = null)
    {
        return new ClipConfig(
            "http://localhost:8080",
            "ws://localhost:8080/clipsocket",
            "alice",
            string.Empty,
            "csrf",
            "JSESSIONID=abc",
            ClipConfig.DefaultMaxSizeBytes,
            ClipConfig.DefaultHashRounds,
            "salt",
            false,
            false,
            false,
            ClipConfig.DefaultMaxSizeBytes,
            false,
            SettingsData.DefaultUuEvasionDelayMs);
    }

    private static TextSyncEngine CreateEngine(Func<IWebSocketTransport> factory, Func<TimeSpan>? evasionDelay = null)
    {
        return new TextSyncEngine(
            TestConfig(),
            new TestSynchronizationContext(),
            _ => { },
            _ => { },
            transportFactory: factory,
            timeProvider: new ManualTimeProvider(),
            getEvasionDelay: evasionDelay);
    }

    private static async Task WaitForConnectedAsync(FakeWebSocketTransport transport)
    {
        await TestHelpers.WaitUntil(() => transport.Sent.Count >= 1);
        transport.Enqueue("CONNECTED\n\n\0");
        await TestHelpers.WaitUntil(() => transport.Sent.Count >= 2);
        await Task.Delay(30);
    }

    private static void EnqueueClip(FakeWebSocketTransport transport, string text)
    {
        var body = JsonUtil.ClipMessage(text, "text");
        transport.Enqueue("MESSAGE\ncontent-length:" + Encoding.UTF8.GetByteCount(body) + "\n\n" + body + "\0");
    }

    [Fact]
    public async Task OnMessage_EvasionPaused_SkipsClipboardWrite()
    {
        var transport = new FakeWebSocketTransport();
        var writes = 0;
        var engine = CreateEngine(() => transport);
        engine.ClipboardSetAsync = (_, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.CompletedTask;
        };
        engine.Start();
        await WaitForConnectedAsync(transport);

        engine.SetEvasionPaused(true);
        EnqueueClip(transport, "should-not-be-written");
        await Task.Delay(150);

        Assert.Equal(0, writes);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task OnMessage_EvasionResumed_AppliesAgain()
    {
        var transport = new FakeWebSocketTransport();
        var writes = 0;
        var engine = CreateEngine(() => transport);
        engine.ClipboardSetAsync = (_, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.CompletedTask;
        };
        engine.Start();
        await WaitForConnectedAsync(transport);

        engine.SetEvasionPaused(true);
        EnqueueClip(transport, "skipped");
        await Task.Delay(100);
        Assert.Equal(0, writes);

        engine.SetEvasionPaused(false);
        EnqueueClip(transport, "applied");
        await TestHelpers.WaitUntil(() => writes == 1);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task OnMessage_WithEvasionDelay_DefersWriteByConfiguredDelay()
    {
        var transport = new FakeWebSocketTransport();
        var writes = 0;
        var engine = CreateEngine(() => transport, () => TimeSpan.FromMilliseconds(300));
        engine.ClipboardSetAsync = (_, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.CompletedTask;
        };
        engine.Start();
        await WaitForConnectedAsync(transport);

        EnqueueClip(transport, "delayed");
        // 延时未到期：不应写
        await Task.Delay(100);
        Assert.Equal(0, writes);

        // 到期后写入
        await TestHelpers.WaitUntil(() => writes == 1, timeoutMs: 3000);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task OnMessage_EvasionTurnsOnDuringDelay_SkipsWrite()
    {
        var transport = new FakeWebSocketTransport();
        var writes = 0;
        var engine = CreateEngine(() => transport, () => TimeSpan.FromMilliseconds(400));
        engine.ClipboardSetAsync = (_, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.CompletedTask;
        };
        engine.Start();
        await WaitForConnectedAsync(transport);

        EnqueueClip(transport, "should-be-cancelled");
        // 延时途中切到前台规避
        await Task.Delay(80);
        engine.SetEvasionPaused(true);

        await Task.Delay(700);
        Assert.Equal(0, writes);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task OnMessage_ZeroDelay_WritesImmediately()
    {
        var transport = new FakeWebSocketTransport();
        var writes = 0;
        var engine = CreateEngine(() => transport, () => TimeSpan.Zero);
        engine.ClipboardSetAsync = (_, _) =>
        {
            Interlocked.Increment(ref writes);
            return Task.CompletedTask;
        };
        engine.Start();
        await WaitForConnectedAsync(transport);

        EnqueueClip(transport, "immediate");
        await TestHelpers.WaitUntil(() => writes == 1);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task OnMessage_WriteFails_NoClipExeFallbackInsteadBackgroundRetry()
    {
        var transport = new FakeWebSocketTransport();
        var attempts = 0;
        var engine = CreateEngine(() => transport);
        var saved = TextSyncEngine.ApplyRetryDelays;
        try
        {
            // 缩短后台重试档位以便测试
            TextSyncEngine.ApplyRetryDelays = [TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30)];
            engine.ClipboardSetAsync = (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                // 同步重试的全部次数都失败，之后后台重试成功
                if (attempts > 5)
                {
                    return Task.CompletedTask;
                }
                throw new System.Runtime.InteropServices.ExternalException("locked");
            };
            engine.Start();
            await WaitForConnectedAsync(transport);

            EnqueueClip(transport, "retried");
            // 5 次同步重试 + 至少一次后台重试
            await TestHelpers.WaitUntil(() => attempts >= 6, timeoutMs: 5000);
            await engine.DisposeAsync();
            Assert.True(attempts >= 6);
        }
        finally
        {
            TextSyncEngine.ApplyRetryDelays = saved;
        }
    }

    [Fact]
    public async Task OnMessage_BackgroundRetryAbandonedWhenGenerationAdvances()
    {
        var transport = new FakeWebSocketTransport();
        var attempts = 0;
        var applied = new List<string>();
        var engine = CreateEngine(() => transport);
        var saved = TextSyncEngine.ApplyRetryDelays;
        try
        {
            TextSyncEngine.ApplyRetryDelays = [TimeSpan.FromMilliseconds(250)];
            engine.ClipboardSetAsync = (text, _) =>
            {
                Interlocked.Increment(ref attempts);
                // 第一次入站全部失败（含同步重试），其余成功
                if (text == "first" )
                {
                    throw new System.Runtime.InteropServices.ExternalException("locked");
                }
                applied.Add(text);
                return Task.CompletedTask;
            };
            engine.Start();
            await WaitForConnectedAsync(transport);

            // 第一条写入失败 → 排入后台重试
            EnqueueClip(transport, "first");
            await TestHelpers.WaitUntil(() => attempts >= 5);

            // 在后台重试到期前，本地发送提交新内容，代际推进
            engine.SendLocalText("local-newer", UiText.ClipboardSource);
            await TestHelpers.WaitUntil(() => transport.Sent.Count(bytes =>
                Encoding.UTF8.GetString(bytes).StartsWith("SEND\n", StringComparison.Ordinal)) == 1);

            var attemptsBefore = attempts;
            // 后台重试到期后应因代际推进而放弃，不再尝试写入 "first"
            await Task.Delay(600);
            Assert.Equal(attemptsBefore, attempts);
            Assert.DoesNotContain("first", applied);

            await engine.DisposeAsync();
        }
        finally
        {
            TextSyncEngine.ApplyRetryDelays = saved;
        }
    }
}
