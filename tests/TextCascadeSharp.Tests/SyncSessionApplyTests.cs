using System.Runtime.InteropServices;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

// 写入重试收归会话层后的关键不变量：
//   1) 版本检查与写入不跨 await —— 旧版本醒来后不会覆盖已写入的新版本
//   2) 版本回调只在实际推进时触发，不会把持久化游标写回旧值
//   3) 暂停期间不在后台重试里改写剪贴板
public class SyncSessionApplyTests
{
    private static ClipConfig Config() => new(
        "https://your-server:8443", "tok", null, "alice", "uuid-1", "PC", 0,
        524_288, 10, 25, 30, ClipConfig.DefaultHashRounds, "salt", "",
        CipherEnabled: false, TrustAllCertificates: false, ServerCertificateThumbprint: "",
        RelaunchOnBoot: false, WebsocketStatusNotification: false, LocalMaxClipboardBytes: 524_288);

    private sealed class Gate
    {
        public readonly List<string> Writes = [];
        public readonly List<ulong> Advanced = [];
        public Func<string, bool> ShouldFail = _ => false;

        public ClipboardBridge Bridge() => new(
            new TestSynchronizationContext(),
            setOverride: (text, _) =>
            {
                if (ShouldFail(text))
                {
                    throw new ExternalException("locked");
                }
                lock (Writes)
                {
                    Writes.Add(text);
                }
                return Task.CompletedTask;
            },
            getOverride: () => string.Empty);
    }

    private static InboundClipMessage Clip(ulong version, string text) => new(
        Version: version,
        Payload: text,
        Encrypted: false,
        Hash: HashUtil.Fnv1A64Hex(text),
        FromClientId: "uuid-2");

    // 版本推进后才允许旧版本重试成功：旧版本必须放弃，且不能把游标写回旧值
    [Fact]
    public async Task RetryApply_OldVersionArrivesAfterNewer_SkipsWriteAndDoesNotRegressCursor()
    {
        var originalDelays = SyncSession.ApplyRetryDelays;
        var originalShort = SyncSession.ShortRetryAttempts;
        SyncSession.ApplyRetryDelays = [TimeSpan.FromMilliseconds(30)];
        SyncSession.ShortRetryAttempts = 1;
        try
        {
            var gate = new Gate();
            var failV5 = true;
            gate.ShouldFail = text => text == "v5" && failV5;

            var session = new SyncSession(
                Config(), gate.Bridge(),
                _ => { }, _ => { },
                version =>
                {
                    lock (gate.Advanced)
                    {
                        gate.Advanced.Add(version);
                    }
                });

            // v5 写入失败 → 进入后台重试
            await session.OnClipAsync(Clip(5, "v5"));
            await TestHelpers.WaitUntil(() => !gate.Writes.Contains("v5"));

            // 后台重试等待期间，更新的 v6 写入成功
            failV5 = false;
            await session.OnClipAsync(Clip(6, "v6"));

            // 让 v5 的后台重试跑完
            await Task.Delay(200);

            lock (gate.Writes)
            {
                // v6 是最终内容；v5 不得覆盖它
                Assert.Equal(["v6"], gate.Writes);
            }
            Assert.Equal(6UL, session.LastServerVersion);

            // 版本回调绝不能包含旧版本 5 之后再写 6 造成回退
            lock (gate.Advanced)
            {
                Assert.DoesNotContain(5UL, gate.Advanced);
            }
        }
        finally
        {
            SyncSession.ApplyRetryDelays = originalDelays;
            SyncSession.ShortRetryAttempts = originalShort;
        }
    }

    // 暂停期间的后台重试不得改写剪贴板
    [Fact]
    public async Task RetryApply_WhilePaused_DoesNotWriteClipboard()
    {
        var originalDelays = SyncSession.ApplyRetryDelays;
        var originalShort = SyncSession.ShortRetryAttempts;
        SyncSession.ApplyRetryDelays = [TimeSpan.FromMilliseconds(30)];
        SyncSession.ShortRetryAttempts = 1;
        try
        {
            var gate = new Gate { ShouldFail = _ => true };
            var paused = false;

            var session = new SyncSession(
                Config(), gate.Bridge(),
                _ => { }, _ => { },
                timeProvider: null,
                currentSenderProvider: null,
                isPaused: () => paused);

            await session.OnClipAsync(Clip(7, "remote"));
            paused = true;

            await Task.Delay(200);

            lock (gate.Writes)
            {
                Assert.Empty(gate.Writes);
            }
        }
        finally
        {
            SyncSession.ApplyRetryDelays = originalDelays;
            SyncSession.ShortRetryAttempts = originalShort;
        }
    }

    // 短暂占用可在会话层短退避内恢复，不必进入后台重试
    [Fact]
    public async Task Apply_TransientLock_RecoversWithinShortRetries()
    {
        var originalShort = SyncSession.ShortRetryAttempts;
        SyncSession.ShortRetryAttempts = 3;
        try
        {
            var gate = new Gate();
            var attempts = 0;
            gate.ShouldFail = _ => Interlocked.Increment(ref attempts) < 3;

            var session = new SyncSession(Config(), gate.Bridge(), _ => { }, _ => { });

            await session.OnClipAsync(Clip(1, "recovered"));

            lock (gate.Writes)
            {
                Assert.Equal(["recovered"], gate.Writes);
            }
            Assert.Equal(1UL, session.LastServerVersion);
        }
        finally
        {
            SyncSession.ShortRetryAttempts = originalShort;
        }
    }
}
