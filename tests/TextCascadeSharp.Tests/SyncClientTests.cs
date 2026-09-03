using System.Net;
using System.Net.WebSockets;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

// textcascade.v1 客户端：建连参数、握手错误分类、消息分发、看门狗。
public class SyncClientTests
{
    private static ClipConfig TestConfig(int heartbeatIntervalSeconds = 30)
    {
        return new ClipConfig(
            "https://your-server:8443",
            "tok",
            null,
            "alice",
            "uuid-1",
            "PC",
            0,
            ClipConfig.DefaultMaxTextBytes,
            10,
            heartbeatIntervalSeconds,
            60,
            ClipConfig.DefaultHashRounds,
            "salt",
            "",
            CipherEnabled: false,
            TrustAllCertificates: false,
            ServerCertificateThumbprint: "",
            RelaunchOnBoot: false,
            WebsocketStatusNotification: false,
            LocalMaxClipboardBytes: ClipConfig.DefaultMaxTextBytes);
    }

    private static SyncClient CreateClient(
        ClipConfig config,
        TestSyncListener listener,
        FakeWebSocketTransport? transport = null,
        ManualTimeProvider? timeProvider = null)
    {
        transport ??= new FakeWebSocketTransport();
        timeProvider ??= new ManualTimeProvider();
        return new SyncClient(config, "tok", listener, () => transport, timeProvider);
    }

    [Fact]
    public async Task ConnectAsync_UsesBearerAndSubprotocol()
    {
        var transport = new FakeWebSocketTransport();
        var client = CreateClient(TestConfig(), new TestSyncListener(), transport);

        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal(1, transport.ConnectCallCount);
        Assert.Equal(new Uri("wss://your-server:8443/api/v1/sync"), transport.LastConnectUri);
        Assert.Equal("tok", transport.LastBearerToken);
        Assert.Equal("textcascade.v1", transport.LastSubProtocol);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task ConnectAsync_Unauthorized_ThrowsSessionExpired()
    {
        var transport = new FakeWebSocketTransport(HttpStatusCode.Unauthorized);
        var client = CreateClient(TestConfig(), new TestSyncListener(), transport);

        await Assert.ThrowsAsync<SessionExpiredException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, transport.LastHttpStatusCode);
    }

    [Fact]
    public async Task ConnectAsync_SubprotocolRejected_ThrowsFatalProtocol()
    {
        var transport = new FakeWebSocketTransport(HttpStatusCode.BadRequest);
        var client = CreateClient(TestConfig(), new TestSyncListener(), transport);

        await Assert.ThrowsAsync<FatalProtocolException>(() => client.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_HandshakeTimeout_Cancels()
    {
        SyncClient.HandshakeTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            var transport = new FakeWebSocketTransport { BlockConnect = true };
            var client = CreateClient(TestConfig(), new TestSyncListener(), transport);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(CancellationToken.None));
        }
        finally
        {
            SyncClient.HandshakeTimeout = TimeSpan.FromSeconds(15);
        }
    }

    [Fact]
    public async Task Dispatch_RoutesContractMessagesToListener()
    {
        var listener = new TestSyncListener();
        var transport = new FakeWebSocketTransport();
        var client = CreateClient(TestConfig(), listener, transport);
        await client.ConnectAsync(CancellationToken.None);

        transport.Enqueue(ContractSamples.WelcomeWithLatest);
        await listener.WelcomeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transport.Enqueue(ContractSamples.ClipBroadcast);
        transport.Enqueue(ContractSamples.ClipAck);
        transport.Enqueue(ContractSamples.Ping);
        transport.Enqueue(ContractSamples.Bye);
        transport.Enqueue(ContractSamples.ErrorTextTooLarge);
        await TestHelpers.WaitUntil(() => listener.Errors.Count == 1);

        Assert.Single(listener.Clips);
        Assert.Equal(10UL, listener.Clips[0].Version);
        var ack = Assert.Single(listener.Acks);
        Assert.Equal("clip-0001", ack.Id);
        Assert.Single(listener.Pings);
        var bye = Assert.Single(listener.Byes);
        Assert.Equal("server_shutdown", bye.Reason);
        var error = Assert.Single(listener.Errors);
        Assert.Equal("text_too_large", error.Code);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Dispatch_MalformedJson_SkipsWithoutError()
    {
        var listener = new TestSyncListener();
        var transport = new FakeWebSocketTransport();
        var client = CreateClient(TestConfig(), listener, transport);
        await client.ConnectAsync(CancellationToken.None);

        transport.Enqueue("this is not json");
        transport.Enqueue("""{"type":"mystery","x":1}""");
        transport.Enqueue("""{"type":"clip","payload":"no version"}""");
        transport.Enqueue(ContractSamples.Ping);
        await TestHelpers.WaitUntil(() => listener.Pings.Count == 1);

        // 畸形/未知消息被跳过，不触发传输错误，连接保持
        Assert.False(listener.TransportErrorTcs.Task.IsCompleted);
        Assert.Single(listener.Pings);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Dispatch_UnknownType_LogsMetadataNotPayload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TextCascadeSyncLog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var logPath = Path.Combine(tempDir, "TextCascade.log");
        var previous = Logger.LogPath;
        Logger.LogPath = logPath;
        try
        {
            var listener = new TestSyncListener();
            var transport = new FakeWebSocketTransport();
            var client = CreateClient(TestConfig(), listener, transport);
            await client.ConnectAsync(CancellationToken.None);

            const string secret = "secret-clipboard-content-must-not-leak";
            transport.Enqueue($$$"""{"type":"mystery","payload":"{{{secret}}}"}""");
            await TestHelpers.WaitUntil(() => File.Exists(logPath) && File.ReadAllText(logPath).Contains("unknown type"));

            var content = File.ReadAllText(logPath);
            Assert.DoesNotContain(secret, content);   // payload 明文绝不落盘
            Assert.Contains("type=mystery", content); // 只记录元数据
            Assert.Contains("length=", content);
            Assert.Contains("bytes", content);
            await client.DisposeAsync();
        }
        finally
        {
            Logger.LogPath = previous;
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ReceiveLoop_RemoteClose_NotifiesListenerWithCloseStatus()
    {
        var listener = new TestSyncListener();
        var transport = new FakeWebSocketTransport();
        var client = CreateClient(TestConfig(), listener, transport);
        await client.ConnectAsync(CancellationToken.None);

        transport.EnqueueClose(WebSocketCloseStatus.EndpointUnavailable);
        await listener.ClosedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var closed = Assert.Single(listener.Events, e => e.StartsWith("closed:"));
        Assert.Contains("EndpointUnavailable", closed);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveLoop_ReassemblesChunkedMessage()
    {
        var listener = new TestSyncListener();
        var transport = new FakeWebSocketTransport();
        var client = CreateClient(TestConfig(), listener, transport);
        await client.ConnectAsync(CancellationToken.None);

        // 构造 > 16KB 的单条 ping 消息：接收缓冲 16KB，需要跨多次 Receive 组装
        var bigServerTime = new string('x', 40_000);
        transport.Enqueue($$$"""{"type":"ping","serverTimeUtc":"{{{bigServerTime}}}"}""");

        await TestHelpers.WaitUntil(() => listener.Pings.Count == 1);
        Assert.Equal(40_000, listener.Pings[0].ServerTimeUtc!.Length);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Watchdog_SilenceBeyondHeartbeatIntervalPlus10s_Aborts()
    {
        var timeProvider = new ManualTimeProvider();
        var listener = new TestSyncListener();
        var transport = new FakeWebSocketTransport();
        // heartbeatIntervalSeconds=30 → 看门狗阈值 40s
        var client = CreateClient(TestConfig(heartbeatIntervalSeconds: 30), listener, transport, timeProvider);
        Assert.Equal(TimeSpan.FromSeconds(40), client.WatchdogTimeout);
        await client.ConnectAsync(CancellationToken.None);

        // 推进 39s：未达阈值，不中断
        timeProvider.Advance(TimeSpan.FromSeconds(39));
        await Task.Delay(50);
        Assert.Equal(0, transport.AbortCount);

        // 推进 2s：超过 40s 阈值，Abort 触发
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        await TestHelpers.WaitUntil(() => transport.AbortCount == 1);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Send_WhenTimeoutExpires_AbortsSocketAndThrows()
    {
        var originalTimeout = SyncClient.SendTimeout;
        SyncClient.SendTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            var listener = new TestSyncListener();
            var transport = new FakeWebSocketTransport { BlockSend = true };
            var client = CreateClient(TestConfig(), listener, transport);
            await client.ConnectAsync(CancellationToken.None);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.SendPongAsync(new PongMessage("timeout test"), CancellationToken.None));

            Assert.Equal(1, transport.AbortCount);
            await client.DisposeAsync();
        }
        finally
        {
            SyncClient.SendTimeout = originalTimeout;
        }
    }

    [Fact]
    public async Task Send_ConcurrentSends_AreSerialized()
    {
        var listener = new TestSyncListener();
        var transport = new FakeWebSocketTransport();
        var client = CreateClient(TestConfig(), listener, transport);
        await client.ConnectAsync(CancellationToken.None);

        var sends = Enumerable.Range(0, 50).Select(i =>
            client.SendPongAsync(new PongMessage($"t{i}"), CancellationToken.None));
        await Task.WhenAll(sends);

        Assert.Equal(50, transport.SendCallCount);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task InboundChannel_QueueFull32_DropsOldestMessages()
    {
        var listener = new TestSyncListener();
        var blockTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pinnedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 让第一条 clip 挂起，把 applier 阻塞住，这样后续 34 条 clip 会直接把 channel 撑满并丢弃最旧的
        listener.OnClipHook = async msg =>
        {
            if (msg.Version == 1)
            {
                // hook 运行即证明 version 1 已离开通道进入 applier；若先洪泛再等信号，
                // 会与 applier 竞争，CI 高负载下可能误丢弃 {1, 2} 而非 {2, 3}
                pinnedTcs.TrySetResult();
                await blockTcs.Task;
            }
        };

        var transport = new FakeWebSocketTransport();
        var client = CreateClient(TestConfig(), listener, transport);
        await client.ConnectAsync(CancellationToken.None);

        transport.Enqueue($$$"""{"type":"clip","version":1,"payload":"text1","encrypted":false,"hash":"hash1"}""");
        // 等 applier 取走 version 1（通道已空）后再灌入洪泛，保证被丢弃的确定是 {2, 3}
        await pinnedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        for (var i = 2; i <= 35; i++)
        {
            transport.Enqueue($$$"""{"type":"clip","version":{{{i}}},"payload":"text{{{i}}}","encrypted":false,"hash":"hash{{{i}}}"}""");
        }

        // 等待 34 条洪泛消息在 ReceiveLoop 中被读取并入队（或触发丢弃）
        await Task.Delay(100);

        // 放行第一条
        blockTcs.TrySetResult();

        // 等待所有消息被消费
        await TestHelpers.WaitUntil(() =>
        {
            lock (listener.Clips)
            {
                return listener.Clips.Any(c => c.Version == 35);
            }
        });

        lock (listener.Clips)
        {
            // 收到 version 1（被 applier 提前读出消费），其余 34 条入 32 容量队列，丢弃了 2 条（version 2, 3）
            Assert.True(listener.Clips.Count <= 33);
            Assert.Equal(35UL, listener.Clips.Last().Version);
            Assert.DoesNotContain(listener.Clips, c => c.Version == 2);
            Assert.DoesNotContain(listener.Clips, c => c.Version == 3);
        }

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CloseAsync_ClosesSocketOnce()
    {
        var transport = new FakeWebSocketTransport();
        var client = CreateClient(TestConfig(), new TestSyncListener(), transport);
        await client.ConnectAsync(CancellationToken.None);

        await client.CloseAsync();
        await client.DisposeAsync();

        Assert.Equal(1, transport.CloseCount);
        Assert.Equal(1, transport.DisposedCount);
    }
}

