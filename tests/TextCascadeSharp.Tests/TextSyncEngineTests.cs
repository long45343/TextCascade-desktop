using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

// 引擎状态机测试（假传输）：hello/welcome/clip/clip_ack/ping/bye/error 各场景、
// 去重、回显抑制、退避与唤醒、会话失效、关停。
public class TextSyncEngineTests
{
    private static ClipConfig TestConfig(
        bool cipherEnabled = false,
        string derivedKeyBase64 = "",
        long maxTextBytes = ClipConfig.DefaultMaxTextBytes,
        ulong lastServerVersion = 0,
        DateTime? tokenExpiresAtUtc = null)
    {
        return new ClipConfig(
            "https://localhosts:8443",
            "tok",
            tokenExpiresAtUtc,
            "alice",
            "uuid-1",
            "PC",
            lastServerVersion,
            maxTextBytes,
            10,
            25,
            30,
            ClipConfig.DefaultHashRounds,
            "salt",
            derivedKeyBase64,
            cipherEnabled,
            TrustAllCertificates: false,
            RelaunchOnBoot: false,
            WebsocketStatusNotification: false,
            LocalMaxClipboardBytes: maxTextBytes);
    }

    // 判断某个状态信封的领域码是否为 code（Core 层契约：Statuses 收的是 CoreStatus.Pack 信封）
    private static bool HasCode(string status, string code)
        => CoreStatus.TryUnpack(status, out var c, out _) && c == code;

    private sealed class EngineHarness
    {
        public TransportFactory Factory = new();
        public List<string> Statuses = [];
        public List<string> Applied = [];
        public TaskCompletionSource SessionExpiredTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(bool connected, int count)> ConnectionChanges = [];
        public string ClipboardText = "";
        public List<string> ClipboardWrites = [];
        public ManualTimeProvider TimeProvider = new();
        public List<ulong> AdvancedVersions = [];

        public TextSyncEngine Create(
            ClipConfig? config = null,
            TimeSpan? reconnectDelay = null,
            HttpStatusCode? handshakeStatus = null)
        {
            if (handshakeStatus is not null)
            {
                Factory = new TransportFactory(handshakeStatus);
            }
            var statuses = Statuses;
            var reconnectPolicy = new ReconnectPolicy(TimeProvider);
            if (reconnectDelay is { } delay)
            {
                reconnectPolicy.DelayOverride = delay;
            }
            var clipboard = new ClipboardBridge(
                new TestSynchronizationContext(),
                setOverride: (text, _) =>
                {
                    lock (ClipboardWrites)
                    {
                        ClipboardWrites.Add(text);
                    }
                    return Task.CompletedTask;
                },
                getOverride: () => ClipboardText);
            var engine = new TextSyncEngine(
                config ?? TestConfig(),
                new TestSynchronizationContext(),
                status =>
                {
                    lock (statuses)
                    {
                        statuses.Add(status);
                    }
                },
                text =>
                {
                    lock (Applied)
                    {
                        Applied.Add(text);
                    }
                },
                () =>
                {
                    SessionExpiredTcs.TrySetResult();
                    return Task.CompletedTask;
                },
                connected =>
                {
                    lock (ConnectionChanges)
                    {
                        ConnectionChanges.Add((connected, ConnectionChanges.Count));
                    }
                },
                Factory.Create,
                TimeProvider,
                version =>
                {
                    lock (AdvancedVersions)
                    {
                        AdvancedVersions.Add(version);
                    }
                },
                reconnectPolicy,
                clipboard);
            return engine;
        }
    }

    private static async Task<FakeWebSocketTransport> WaitForConnectedAsync(EngineHarness harness)
    {
        await TestHelpers.WaitUntil(() => harness.Factory.CreatedCount >= 1);
        var transport = harness.Factory.Last;
        await TestHelpers.WaitUntil(() => transport.SentTexts().Any(static t => t.Contains("\"type\":\"hello\"")));
        // 等待引擎把 _connected 置位（hello 发出后的同步收尾）
        await Task.Delay(50);
        return transport;
    }

    private static string SentJson(FakeWebSocketTransport transport, string type)
    {
        return transport.SentTexts().Single(t => t.Contains($"\"type\":\"{type}\""));
    }

    // ---------- hello ----------

    [Fact]
    public async Task Start_ConnectsAndSendsHelloWithoutSnapshot()
    {
        var harness = new EngineHarness { ClipboardText = "" };
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        Assert.Equal(new Uri("wss://localhosts:8443/api/v1/sync"), transport.LastConnectUri);
        Assert.Equal("tok", transport.LastBearerToken);
        Assert.Equal("textcascade.v1", transport.LastSubProtocol);

        var hello = SentJson(transport, "hello");
        Assert.Contains("\"clientId\":\"uuid-1\"", hello);
        Assert.Contains("\"clientName\":\"PC\"", hello);
        Assert.Contains("\"lastServerVersion\":0", hello);
        Assert.DoesNotContain("snapshot", hello);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Start_SendsHelloWithPlaintextSnapshot()
    {
        var harness = new EngineHarness { ClipboardText = "local text" };
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        var hello = SentJson(transport, "hello");
        Assert.Contains("\"snapshot\":{", hello);
        Assert.Contains("\"payload\":\"local text\"", hello);
        Assert.Contains("\"encrypted\":false", hello);
        Assert.Contains($"\"hash\":\"{HashUtil.Fnv1A64Hex("local text")}\"", hello);
        Assert.Matches("\"localModifiedAtUtc\":\"[^\"]+Z\"", hello);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Start_SendsHelloWithEncryptedSnapshot()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var harness = new EngineHarness { ClipboardText = "secret text" };
        await using var engine = harness.Create(TestConfig(cipherEnabled: true, derivedKeyBase64: key));
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        var hello = SentJson(transport, "hello");
        Assert.Contains("\"encrypted\":true", hello);
        // snapshot.payload 是加密 JSON，用派生密钥可解回明文
        using var document = JsonDocument.Parse(hello);
        var payload = document.RootElement.GetProperty("snapshot").GetProperty("payload").GetString()!;
        Assert.Equal("secret text", CryptoManager.Decrypt(JsonUtil.ParseEncryptedPayload(payload), key));
        await engine.DisposeAsync();
    }

    // ---------- welcome / clip 接收 ----------

    [Fact]
    public async Task Welcome_AppliesNewerLatest_WritesClipboardAndSuppressesEcho()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        transport.Enqueue(ContractSamples.WelcomeWithLatest); // version 9, "hello"
        await TestHelpers.WaitUntil(() => harness.ClipboardWrites.Count == 1);
        Assert.Equal("hello", harness.ClipboardWrites[0]);
        lock (harness.Applied)
        {
            Assert.Contains("hello", harness.Applied);
        }

        // 回显抑制：远端写入触发的下一次本地事件被跳过
        engine.SendLocalText("hello", "clipboard");
        await Task.Delay(100);
        Assert.Single(transport.SentTexts()); // 只有 hello，没有 clip

        // version 游标推进：同 version 的 clip 不再应用
        transport.Enqueue("""{"type":"clip","version":9,"payload":"hello","encrypted":false,"hash":"af63dc4c8601ec8c"}""");
        await Task.Delay(100);
        Assert.Single(harness.ClipboardWrites);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Welcome_LatestNull_DoesNothing()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        transport.Enqueue(ContractSamples.WelcomeEmpty);
        await Task.Delay(100);
        Assert.Empty(harness.ClipboardWrites);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task ClipApplies_WhenVersionNewerAndHashNotLocallySent()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        transport.Enqueue("""{"type":"clip","version":5,"payload":"from remote","encrypted":false,"hash":"aaaabbbbccccdddd"}""");
        await TestHelpers.WaitUntil(() => harness.ClipboardWrites.Count == 1);
        Assert.Equal("from remote", harness.ClipboardWrites[0]);

        // clip_ack 推进游标到 11 → 之后 version 10 的旧 clip 被忽略（最新值语义防重复）
        transport.Enqueue(ContractSamples.ClipAck); // version 11
        transport.Enqueue("""{"type":"clip","version":10,"payload":"older","encrypted":false,"hash":"1111222233334444"}""");
        await Task.Delay(100);
        Assert.Single(harness.ClipboardWrites);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task ClipEcho_OfLocallySentText_IsNotApplied()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        engine.SendLocalText("abc", "clipboard");
        await TestHelpers.WaitUntil(() => transport.SentTexts().Any(static t => t.Contains("\"type\":\"clip\"")));
        var hash = HashUtil.Fnv1A64Hex("abc");

        // 服务端把本端发出的内容广播回来：hash 相同 → 不写剪贴板
        transport.Enqueue($$$"""{"type":"clip","version":3,"payload":"abc","encrypted":false,"hash":"{{{hash}}}"}""");
        await Task.Delay(100);
        Assert.Empty(harness.ClipboardWrites);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task ClipEncrypted_DecryptsWithDerivedKey()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var harness = new EngineHarness();
        await using var engine = harness.Create(TestConfig(cipherEnabled: true, derivedKeyBase64: key));
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        var payload = JsonUtil.EncryptedPayload(CryptoManager.Encrypt("cipher remote", key));
        // payload 是含引号的 JSON 字符串，需作为转义后的 JSON 字符串字面量嵌入
        var payloadLiteral = System.Text.Json.JsonSerializer.Serialize(payload);
        transport.Enqueue($$$"""{"type":"clip","version":4,"payload":{{{payloadLiteral}}},"encrypted":true,"hash":"ffff0000ffff0000"}""");
        await TestHelpers.WaitUntil(() => harness.ClipboardWrites.Count == 1);
        Assert.Equal("cipher remote", harness.ClipboardWrites[0]);
        await engine.DisposeAsync();
    }

    // ---------- 本地发送 ----------

    [Fact]
    public async Task SendLocalText_SendsClipWithUuidAndHash()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        engine.SendLocalText("outbound", "clipboard");
        await TestHelpers.WaitUntil(() => transport.SentTexts().Any(static t => t.Contains("\"type\":\"clip\"")));

        var clip = SentJson(transport, "clip");
        Assert.Contains("\"payload\":\"outbound\"", clip);
        Assert.Contains("\"encrypted\":false", clip);
        Assert.Contains($"\"hash\":\"{HashUtil.Fnv1A64Hex("outbound")}\"", clip);
        // id 为 UUID 形式
        using var document = JsonDocument.Parse(clip);
        var id = document.RootElement.GetProperty("id").GetString()!;
        Assert.True(Guid.TryParseExact(id, "N", out _) || Guid.TryParseExact(id, "D", out _));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task SendLocalText_SameAsLastRemoteText_NotSent()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        // 入站 hash 必须是内容的真实 FNV-1a，这样引擎本地计算的 hash 才能匹配
        var remoteHash = HashUtil.Fnv1A64Hex("same");
        transport.Enqueue($$$"""{"type":"clip","version":2,"payload":"same","encrypted":false,"hash":"{{{remoteHash}}}"}""");
        await TestHelpers.WaitUntil(() => harness.ClipboardWrites.Count == 1);
        // 远端写入后的第一次本地事件被抑制（消费抑制标记）
        engine.SendLocalText("next", "clipboard");
        await Task.Delay(50);
        // 第二次本地事件正常广播
        engine.SendLocalText("next", "clipboard");
        await TestHelpers.WaitUntil(() => transport.SentTexts().Any(static t => t.Contains("\"type\":\"clip\"")));

        // 与最近远端内容相同 → 不广播
        engine.SendLocalText("same", "clipboard");
        await Task.Delay(100);
        Assert.Equal(1, transport.SentTexts().Count(static t => t.Contains("\"type\":\"clip\"")));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task SendLocalText_NotConnected_IgnoredWithStatus()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create();
        // 未 Start：未连接
        engine.SendLocalText("text", "clipboard");
        await Task.Delay(100);
        lock (harness.Statuses)
        {
            Assert.Contains(harness.Statuses, s => HasCode(s, ErrorCodes.IgnoredNotConnected));
        }
        Assert.Equal(0, harness.Factory.CreatedCount); // 不建连
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task SendLocalText_TooLarge_NotSent()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create(TestConfig(maxTextBytes: 10));
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        engine.SendLocalText(new string('x', 100), "clipboard");
        await Task.Delay(100);
        Assert.DoesNotContain(transport.SentTexts(), static t => t.Contains("\"type\":\"clip\""));
        lock (harness.Statuses)
        {
            Assert.Contains(harness.Statuses, s => HasCode(s, ErrorCodes.ClipboardTooLarge));
        }
        await engine.DisposeAsync();
    }

    // ---------- 心跳 ----------

    [Fact]
    public async Task Ping_RepliesPongWithClientTimeZ()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        transport.Enqueue(ContractSamples.Ping);
        await TestHelpers.WaitUntil(() => transport.SentTexts().Any(static t => t.Contains("\"type\":\"pong\"")));

        var pong = SentJson(transport, "pong");
        Assert.Matches("\"clientTimeUtc\":\"[^\"]+Z\"", pong);
        await engine.DisposeAsync();
    }

    // ---------- 错误帧 ----------

    [Fact]
    public async Task ErrorTextTooLarge_OnlyStatusHint_ConnectionKept()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        transport.Enqueue(ContractSamples.ErrorTextTooLarge);
        await TestHelpers.WaitUntil(() =>
        {
            lock (harness.Statuses)
            {
                return harness.Statuses.Any(s => HasCode(s, ErrorCodes.TextTooLargeIgnored));
            }
        });
        // 连接保持：仍可继续收消息
        transport.Enqueue(ContractSamples.Ping);
        await TestHelpers.WaitUntil(() => transport.SentTexts().Any(static t => t.Contains("\"type\":\"pong\"")));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task ErrorRateLimited_PausesLocalSendsForAbout1s()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        engine.SendLocalText("first", "clipboard");
        await TestHelpers.WaitUntil(() => transport.SentTexts().Any(static t => t.Contains("\"type\":\"clip\"")));

        transport.Enqueue(ContractSamples.ErrorRateLimited);
        await TestHelpers.WaitUntil(() =>
        {
            lock (harness.Statuses)
            {
                return harness.Statuses.Any(s => HasCode(s, ErrorCodes.RateLimitedPaused));
            }
        });

        // 暂停窗口内的发送被丢弃
        engine.SendLocalText("paused", "clipboard");
        await Task.Delay(100);
        Assert.Equal(1, transport.SentTexts().Count(static t => t.Contains("\"type\":\"clip\"")));
        await engine.DisposeAsync();
    }

    // ---------- 重连与退避 ----------

    [Theory]
    [InlineData(false, 1, 1)]
    [InlineData(false, 2, 2)]
    [InlineData(false, 3, 5)]
    [InlineData(false, 4, 10)]
    [InlineData(false, 5, 30)]
    [InlineData(false, 6, 60)]
    [InlineData(false, 7, 60)]
    [InlineData(false, 100, 60)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 2, 2)]
    [InlineData(true, 3, 5)]
    [InlineData(true, 4, 10)]
    [InlineData(true, 5, 10)]
    [InlineData(true, 100, 10)]
    public void BackoffDelay_FollowsSpecSequences(bool gentle, int attempt, int expectedSeconds)
    {
        var strategy = gentle ? BackoffStrategy.GentleReconnect : BackoffStrategy.NormalReconnect;
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), strategy.GetDelay(attempt));
    }

    [Fact]
    public async Task TransportClose_ReconnectsWithBackoff()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create(reconnectDelay: TimeSpan.FromMilliseconds(50));
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        transport.EnqueueClose(null); // 普通断开
        await TestHelpers.WaitUntil(() => harness.Factory.CreatedCount == 2);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Bye_ThenClose1001_ReconnectsGently()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create(reconnectDelay: TimeSpan.FromMilliseconds(50));
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        transport.Enqueue(ContractSamples.Bye);
        await Task.Delay(50);
        transport.EnqueueClose(WebSocketCloseStatus.EndpointUnavailable); // 1001
        await TestHelpers.WaitUntil(() => harness.Factory.CreatedCount == 2);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Welcome_ResetsBackoff()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create(reconnectDelay: TimeSpan.FromMilliseconds(50));
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        transport.EnqueueClose(null);
        await TestHelpers.WaitUntil(() => harness.Factory.CreatedCount == 2);
        // 重连成功并收到 welcome 后，退避计数应重置：
        // 下一次断开仍按首档快速重连（这里主要验证 welcome 路径不抛、连接可用）
        var second = harness.Factory.Last;
        await TestHelpers.WaitUntil(() => second.SentTexts().Any(static t => t.Contains("\"type\":\"hello\"")));
        second.Enqueue(ContractSamples.WelcomeEmpty);
        await Task.Delay(50);
        second.EnqueueClose(null);
        await TestHelpers.WaitUntil(() => harness.Factory.CreatedCount == 3);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Stop_DoesNotReconnect()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create(reconnectDelay: TimeSpan.FromMilliseconds(50));
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        await engine.StopAsync(); // 用户主动停止：close 1000，不再重连
        transport.EnqueueClose(null);
        await Task.Delay(200);
        Assert.Equal(1, harness.Factory.CreatedCount);
        Assert.Equal(1, transport.CloseCount);
    }

    [Fact]
    public async Task SessionExpired401_StopsReconnectAndNotifies()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create(handshakeStatus: HttpStatusCode.Unauthorized);
        engine.Start();

        await harness.SessionExpiredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        Assert.Equal(1, harness.Factory.CreatedCount); // 不再重连
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task FatalSubprotocol400_SuspendsReconnect()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create(handshakeStatus: HttpStatusCode.BadRequest);
        engine.Start();

        await TestHelpers.WaitUntil(() => harness.Factory.CreatedCount == 1);
        await Task.Delay(200);
        Assert.Equal(1, harness.Factory.CreatedCount); // 致命错误：停止重连
        lock (harness.Statuses)
        {
            Assert.Contains(harness.Statuses, s => HasCode(s, ErrorCodes.FatalProtocolError));
        }
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task TokenNearlyExpired_TriggersSessionRecoveryWithoutConnecting()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create(TestConfig(tokenExpiresAtUtc: DateTime.UtcNow.AddSeconds(5)));
        engine.Start();

        await harness.SessionExpiredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, harness.Factory.CreatedCount); // 未尝试建立 WebSocket
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task NotifyWake_DuringBackoff_ReconnectsImmediately()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create(reconnectDelay: TimeSpan.FromSeconds(30));
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        transport.EnqueueClose(null);
        // 退避 30s 等待中：确认未重连
        await Task.Delay(150);
        Assert.Equal(1, harness.Factory.CreatedCount);

        // 电源/网络恢复：1-2s 内重连
        engine.NotifyWake();
        await TestHelpers.WaitUntil(() => harness.Factory.CreatedCount == 2, timeoutMs: 4000);
        await engine.DisposeAsync();
    }

    // ---------- 版本游标持久化 ----------

    [Fact]
    public async Task ServerVersionAdvanced_CallbackFiredOnWelcomeClipAndAck()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        // welcome.latest 应用成功 → 推进并回调
        transport.Enqueue(ContractSamples.WelcomeWithLatest); // version 9
        await TestHelpers.WaitUntil(() =>
        {
            lock (harness.AdvancedVersions)
            {
                return harness.AdvancedVersions.Contains(9UL);
            }
        });

        // clip_ack → 推进并回调
        transport.Enqueue(ContractSamples.ClipAck); // version 11
        await TestHelpers.WaitUntil(() =>
        {
            lock (harness.AdvancedVersions)
            {
                return harness.AdvancedVersions.Contains(11UL);
            }
        });

        // 旧版本（≤ 游标）不触发回调
        transport.Enqueue("""{"type":"clip_ack","id":"x","version":10}""");
        await Task.Delay(100);
        lock (harness.AdvancedVersions)
        {
            Assert.Equal([9UL, 11UL], harness.AdvancedVersions);
        }
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task ServerVersionAdvanced_EchoOfOwnClipAdvancesCursor()
    {
        var harness = new EngineHarness();
        await using var engine = harness.Create();
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        engine.SendLocalText("abc", "clipboard");
        await TestHelpers.WaitUntil(() => transport.SentTexts().Any(static t => t.Contains("\"type\":\"clip\"")));
        var hash = HashUtil.Fnv1A64Hex("abc");

        // 本端发出的内容回环：不写剪贴板，但游标推进并回调
        transport.Enqueue($$$"""{"type":"clip","version":3,"payload":"abc","encrypted":false,"hash":"{{{hash}}}"}""");
        await TestHelpers.WaitUntil(() =>
        {
            lock (harness.AdvancedVersions)
            {
                return harness.AdvancedVersions.Contains(3UL);
            }
        });
        Assert.Empty(harness.ClipboardWrites);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task ServerVersion_RestoredFromConfigOnInit()
    {
        // 重启场景：持久化的 lastServerVersion 进入引擎初始游标，
        // 旧版本 welcome.latest 不再被应用
        var harness = new EngineHarness();
        await using var engine = harness.Create(TestConfig(lastServerVersion: 42UL));
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        var hello = SentJson(transport, "hello");
        Assert.Contains("\"lastServerVersion\":42", hello);

        transport.Enqueue("""{"type":"clip","version":41,"payload":"old","encrypted":false,"hash":"1234567890abcdef"}""");
        await Task.Delay(100);
        Assert.Empty(harness.ClipboardWrites);
        await engine.DisposeAsync();
    }

    // ---------- 加密互通 ----------

    [Fact]
    public async Task SendLocalText_EncryptedClipDecryptable()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var harness = new EngineHarness();
        await using var engine = harness.Create(TestConfig(cipherEnabled: true, derivedKeyBase64: key));
        engine.Start();
        var transport = await WaitForConnectedAsync(harness);

        engine.SendLocalText("encrypted outbound", "clipboard");
        await TestHelpers.WaitUntil(() => transport.SentTexts().Any(static t => t.Contains("\"type\":\"clip\"")));

        var clip = SentJson(transport, "clip");
        Assert.Contains("\"encrypted\":true", clip);
        using var document = JsonDocument.Parse(clip);
        var payload = document.RootElement.GetProperty("payload").GetString()!;
        Assert.Equal("encrypted outbound", CryptoManager.Decrypt(JsonUtil.ParseEncryptedPayload(payload), key));
        await engine.DisposeAsync();
    }

    // ---------- 剪贴板写入重试 ----------

    [Fact]
    public async Task SetClipboardWithRetry_SucceedsFirstAttempt()
    {
        var calls = 0;
        var written = await ClipboardBridge.SetClipboardWithRetryAsync(
            "text", (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            });
        Assert.True(written);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SetClipboardWithRetry_RetriesOnExternalException()
    {
        var calls = 0;
        var written = await ClipboardBridge.SetClipboardWithRetryAsync(
            "text", (_, _) =>
            {
                if (Interlocked.Increment(ref calls) < 3)
                {
                    throw new ExternalException("clipboard locked");
                }
                return Task.CompletedTask;
            }, retryDelay: TimeSpan.FromMilliseconds(10));
        Assert.True(written);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task SetClipboardWithRetry_AllAttemptsFail_ReturnsFalse()
    {
        var calls = 0;
        var written = await ClipboardBridge.SetClipboardWithRetryAsync(
            "text", (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new ExternalException("clipboard locked");
            }, maxAttempts: 3, retryDelay: TimeSpan.FromMilliseconds(10));
        Assert.False(written);
        Assert.Equal(3, calls);
    }

    // ---------- 传输工厂 ----------

    private sealed class TransportFactory
    {
        private readonly object _gate = new();
        private readonly List<FakeWebSocketTransport> _created = [];
        private readonly Queue<System.Net.HttpStatusCode?> _handshakeStatuses = new();

        public TransportFactory(params System.Net.HttpStatusCode?[] statuses)
        {
            foreach (var status in statuses)
            {
                _handshakeStatuses.Enqueue(status);
            }
        }

        public int CreatedCount
        {
            get
            {
                lock (_gate)
                {
                    return _created.Count;
                }
            }
        }

        public FakeWebSocketTransport Last
        {
            get
            {
                lock (_gate)
                {
                    return _created[^1];
                }
            }
        }

        public FakeWebSocketTransport Create()
        {
            System.Net.HttpStatusCode? status = null;
            lock (_gate)
            {
                if (_handshakeStatuses.Count > 0)
                {
                    status = _handshakeStatuses.Dequeue();
                }
                var transport = new FakeWebSocketTransport(status);
                _created.Add(transport);
                return transport;
            }
        }
    }
}
