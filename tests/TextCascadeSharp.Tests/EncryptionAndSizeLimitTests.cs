using System.Text;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

// 加密开关与派生密钥必须一致：开启加密但密钥为空时绝不能明文外发。
// 之前 SyncSession 用 `CipherEnabled && key.Length > 0` 判定，密钥缺失会静默
// 退回 encrypted=false，把明文发给服务端。
public class EncryptionAndSizeLimitTests
{
    private static ClipConfig Config(
        bool cipherEnabled = false,
        string derivedKeyBase64 = "",
        long maxTextBytes = 524_288,
        long localMaxClipboardBytes = 0)
    {
        return new ClipConfig(
            "https://your-server:8443",
            "tok",
            null,
            "alice",
            "uuid-1",
            "PC",
            0,
            maxTextBytes,
            10,
            25,
            30,
            ClipConfig.DefaultHashRounds,
            "salt",
            derivedKeyBase64,
            cipherEnabled,
            TrustAllCertificates: false,
            ServerCertificateThumbprint: "",
            RelaunchOnBoot: false,
            WebsocketStatusNotification: false,
            LocalMaxClipboardBytes: localMaxClipboardBytes > 0 ? localMaxClipboardBytes : maxTextBytes);
    }

    private sealed class RecordingSender : ISyncTransportSender
    {
        public List<OutboundClipMessage> SentClips { get; } = [];

        public Task SendClipAsync(OutboundClipMessage clip, CancellationToken cancellationToken)
        {
            SentClips.Add(clip);
            return Task.CompletedTask;
        }

        public Task SendHelloAsync(HelloMessage hello, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendPongAsync(PongMessage pong, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // 加密开启但密钥为空：不得构造出 encrypted=false 的明文本地发送
    [Fact]
    public async Task SendLocalText_CipherEnabledWithoutKey_ReportsProblemInsteadOfPlaintext()
    {
        var sender = new RecordingSender();
        var session = new SyncSession(
            Config(cipherEnabled: true, derivedKeyBase64: ""),
            new ClipboardBridge(new TestSynchronizationContext(), getOverride: () => ""),
            _ => { },
            _ => { });
        session.SetConnected(true);

        await session.SendLocalTextAsync("secret", "clipboard", sender, CancellationToken.None);

        // 明文绝不能出现在 wire 上
        Assert.Empty(sender.SentClips);
    }

    // 有密钥时正常加密，payload 不是明文
    [Fact]
    public async Task SendLocalText_CipherEnabledWithKey_EncryptsPayload()
    {
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var sender = new RecordingSender();
        var session = new SyncSession(
            Config(cipherEnabled: true, derivedKeyBase64: key),
            new ClipboardBridge(new TestSynchronizationContext(), getOverride: () => ""),
            _ => { },
            _ => { });
        session.SetConnected(true);

        await session.SendLocalTextAsync("secret text", "clipboard", sender, CancellationToken.None);

        var clip = Assert.Single(sender.SentClips);
        Assert.True(clip.Encrypted);
        Assert.DoesNotContain("secret text", clip.Payload);
    }

    // 加密后 payload 超过服务端 max_text_bytes：必须被拦截，不能发出去等着被拒
    [Fact]
    public async Task SendLocalText_EncryptedPayloadExceedingServerLimit_IsRejectedLocally()
    {
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var sender = new RecordingSender();
        var statuses = new List<string>();
        // 服务端默认 max_text_bytes = 524288；明文 400000 字节加密后约 533KB 必然超限
        var config = Config(cipherEnabled: true, derivedKeyBase64: key, maxTextBytes: 524_288);
        var session = new SyncSession(
            config,
            new ClipboardBridge(new TestSynchronizationContext(), getOverride: () => ""),
            status => statuses.Add(status),
            _ => { });
        session.SetConnected(true);

        await session.SendLocalTextAsync(new string('x', 400_000), "clipboard", sender, CancellationToken.None);

        Assert.Empty(sender.SentClips);
        Assert.Contains(statuses, s => CoreStatus.TryUnpack(s, out var code, out _) && code == ErrorCodes.ClipboardTooLarge);
    }

    // 明文含大量换行会把帧撑到两倍以上：按整帧上限拦截，而不是只看 payload
    [Fact]
    public async Task SendLocalText_PlaintextWithEscapesExceedingFrameLimit_IsRejectedLocally()
    {
        var sender = new RecordingSender();
        var statuses = new List<string>();
        var config = Config(cipherEnabled: false, maxTextBytes: 524_288);
        var session = new SyncSession(
            config,
            new ClipboardBridge(new TestSynchronizationContext(), getOverride: () => ""),
            status => statuses.Add(status),
            _ => { });
        session.SetConnected(true);

        // 40 万字节 = 1 个字母 + 399999 个换行（非纯空白，不会被 IsNullOrWhiteSpace 拦掉）
        var text = "a" + new string('\n', 399_999);
        Assert.Equal(400_000, Encoding.UTF8.GetByteCount(text));

        await session.SendLocalTextAsync(text, "clipboard", sender, CancellationToken.None);

        Assert.Empty(sender.SentClips);
        Assert.Contains(statuses, s => CoreStatus.TryUnpack(s, out var code, out _) && code == ErrorCodes.ClipboardTooLarge);
    }

    // hello 快照超限时应降级为"不带 snapshot 正常连接"，而不是发一个必定被服务端拒绝的快照
    [Fact]
    public async Task BuildHelloSnapshot_OversizedClipboard_ReturnsNullInsteadOfOversizedSnapshot()
    {
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var oversized = new string('x', 400_000);
        var session = new SyncSession(
            Config(cipherEnabled: true, derivedKeyBase64: key, maxTextBytes: 524_288),
            new ClipboardBridge(new TestSynchronizationContext(), getOverride: () => oversized),
            _ => { },
            _ => { });

        var snapshot = await session.BuildHelloSnapshotAsync();

        Assert.Null(snapshot);
    }

    // 正常大小的快照仍应携带
    [Fact]
    public async Task BuildHelloSnapshot_NormalClipboard_ReturnsSnapshot()
    {
        var session = new SyncSession(
            Config(cipherEnabled: false),
            new ClipboardBridge(new TestSynchronizationContext(), getOverride: () => "local text"),
            _ => { },
            _ => { });

        var snapshot = await session.BuildHelloSnapshotAsync();

        Assert.NotNull(snapshot);
        Assert.Equal("local text", snapshot!.Payload);
        Assert.False(snapshot.Encrypted);
    }
}
