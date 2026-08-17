using System.Runtime.InteropServices;
using System.Text;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

public class TextSyncEngineTests
{
    private static ClipConfig TestConfig(
        bool cipherEnabled = false,
        string keyBase64 = "",
        long maxSizeBytes = ClipConfig.DefaultMaxSizeBytes)
    {
        return new ClipConfig(
            "http://localhost:8080",
            "ws://localhost:8080/clipsocket",
            "alice",
            keyBase64,
            "csrf",
            "JSESSIONID=abc",
            maxSizeBytes,
            ClipConfig.DefaultHashRounds,
            "salt",
            cipherEnabled,
            false,
            false,
            maxSizeBytes);
    }

    private static TextSyncEngine CreateEngine(
        Func<IWebSocketTransport> transportFactory,
        Action<string>? onStatus = null,
        Func<Task>? onSessionExpired = null,
        Action<bool>? onConnectionChanged = null,
        bool cipherEnabled = false,
        string keyBase64 = "",
        TimeSpan? reconnectDelay = null,
        long maxSizeBytes = ClipConfig.DefaultMaxSizeBytes)
    {
        var engine = new TextSyncEngine(
            TestConfig(cipherEnabled, keyBase64, maxSizeBytes),
            new TestSynchronizationContext(),
            onStatus ?? (_ => { }),
            _ => { },
            onSessionExpired,
            onConnectionChanged,
            transportFactory,
            new ManualTimeProvider());
        if (reconnectDelay is { } delay)
        {
            engine.ReconnectDelayOverride = delay;
        }
        return engine;
    }

    private static async Task WaitForConnectedAsync(FakeWebSocketTransport transport)
    {
        // 等待 CONNECT 发出后回送 CONNECTED；SUBSCRIBE 发出说明引擎已进入 connected 状态
        await TestHelpers.WaitUntil(() => transport.Sent.Count >= 1);
        transport.Enqueue("CONNECTED\n\n\0");
        await TestHelpers.WaitUntil(() => transport.Sent.Count >= 2);
        await Task.Delay(30);
    }

    private static int SendFrameCount(FakeWebSocketTransport transport, string command)
    {
        return transport.Sent.Count(bytes => Encoding.UTF8.GetString(bytes).StartsWith(command + "\n", StringComparison.Ordinal));
    }

    private sealed class TransportFactory
    {
        private readonly object _gate = new();
        private readonly List<FakeWebSocketTransport> _created = new();
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

        public int DisposedCount
        {
            get
            {
                lock (_gate)
                {
                    return _created.Sum(static transport => transport.DisposedCount);
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

        public FakeWebSocketTransport Create(System.Net.HttpStatusCode? handshakeStatus = null)
        {
            System.Net.HttpStatusCode? effective;
            lock (_gate)
            {
                effective = _handshakeStatuses.Count > 0 ? _handshakeStatuses.Dequeue() : handshakeStatus;
            }
            var transport = new FakeWebSocketTransport(effective);
            lock (_gate)
            {
                _created.Add(transport);
            }
            return transport;
        }
    }

    [Fact]
    public async Task SendLocalText_NotConnected_Ignored()
    {
        var transport = new FakeWebSocketTransport();
        var engine = CreateEngine(() => transport);

        engine.SendLocalText("hello", UiText.ClipboardSource);
        await Task.Delay(80);

        Assert.Empty(transport.Sent);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task SendLocalText_SameHashTwice_SecondSuppressed()
    {
        var transport = new FakeWebSocketTransport();
        var engine = CreateEngine(() => transport);
        engine.Start();
        await WaitForConnectedAsync(transport);

        engine.SendLocalText("same", UiText.ClipboardSource);
        await TestHelpers.WaitUntil(() => SendFrameCount(transport, "SEND") == 1);
        engine.SendLocalText("same", UiText.ClipboardSource);
        await Task.Delay(80);

        Assert.Equal(1, SendFrameCount(transport, "SEND"));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task SendLocalText_SendFails_HashNotCommitted_RetryAllowed()
    {
        var transport = new FakeWebSocketTransport();
        var engine = CreateEngine(() => transport);
        engine.Start();
        await WaitForConnectedAsync(transport);

        transport.FailSends = true;
        engine.SendLocalText("same", UiText.ClipboardSource);
        await TestHelpers.WaitUntil(() => transport.SendCallCount >= 1);
        engine.SendLocalText("same", UiText.ClipboardSource);
        await TestHelpers.WaitUntil(() => transport.SendCallCount >= 2);

        await engine.DisposeAsync();
    }

    [Fact]
    public async Task OnMessage_RemoteApply_SetsSuppressNextLocal()
    {
        var transport = new FakeWebSocketTransport();
        var clipboardWrites = 0;
        var engine = CreateEngine(() => transport);
        engine.ClipboardSetAsync = (_, _) =>
        {
            Interlocked.Increment(ref clipboardWrites);
            return Task.CompletedTask;
        };
        engine.Start();
        await WaitForConnectedAsync(transport);

        var body = JsonUtil.ClipMessage("remote", "text");
        transport.Enqueue("MESSAGE\ncontent-length:" + Encoding.UTF8.GetByteCount(body) + "\n\n" + body + "\0");
        await TestHelpers.WaitUntil(() => clipboardWrites == 1);

        engine.SendLocalText("local", UiText.ClipboardSource);
        await Task.Delay(100);

        Assert.Equal(0, SendFrameCount(transport, "SEND"));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task OnMessage_NonTextType_Ignored()
    {
        var transport = new FakeWebSocketTransport();
        var clipboardWrites = 0;
        var engine = CreateEngine(() => transport);
        engine.ClipboardSetAsync = (_, _) =>
        {
            Interlocked.Increment(ref clipboardWrites);
            return Task.CompletedTask;
        };
        engine.Start();
        await WaitForConnectedAsync(transport);

        var body = JsonUtil.ClipMessage("image-data", "image");
        transport.Enqueue("MESSAGE\ncontent-length:" + Encoding.UTF8.GetByteCount(body) + "\n\n" + body + "\0");
        await Task.Delay(100);

        Assert.Equal(0, clipboardWrites);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task OnMessage_OversizeInbound_Dropped_StateUntouched()
    {
        var transport = new FakeWebSocketTransport();
        var clipboardWrites = 0;
        var engine = CreateEngine(() => transport, maxSizeBytes: 100);
        engine.ClipboardSetAsync = (_, _) =>
        {
            Interlocked.Increment(ref clipboardWrites);
            return Task.CompletedTask;
        };
        engine.Start();
        await WaitForConnectedAsync(transport);

        var body = JsonUtil.ClipMessage(new string('x', 200), "text");
        transport.Enqueue("MESSAGE\ncontent-length:" + Encoding.UTF8.GetByteCount(body) + "\n\n" + body + "\0");
        await Task.Delay(100);

        Assert.Equal(0, clipboardWrites);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task OnMessage_BadJson_StatusError_NoCrash()
    {
        var transport = new FakeWebSocketTransport();
        var statuses = new List<string>();
        var engine = CreateEngine(() => transport, onStatus: statuses.Add);
        engine.Start();
        await WaitForConnectedAsync(transport);

        transport.Enqueue("MESSAGE\ncontent-length:8\n\nnot-json\0");
        await TestHelpers.WaitUntil(() => statuses.Any(static s =>
            s.Contains("Inbound", StringComparison.OrdinalIgnoreCase)
            || s.Contains("接收数据失败", StringComparison.Ordinal)));

        await engine.DisposeAsync();
    }

    [Fact]
    public async Task OnMessage_CipherRoundTrip_Applied()
    {
        var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray());
        var transport = new FakeWebSocketTransport();
        string? applied = null;
        var engine = CreateEngine(() => transport, cipherEnabled: true, keyBase64: key);
        engine.ClipboardSetAsync = (text, _) =>
        {
            applied = text;
            return Task.CompletedTask;
        };
        engine.Start();
        await WaitForConnectedAsync(transport);

        var payload = JsonUtil.EncryptedPayload(CryptoManager.Encrypt("secret-text", key));
        var body = JsonUtil.ClipMessage(payload, "text");
        transport.Enqueue("MESSAGE\ncontent-length:" + Encoding.UTF8.GetByteCount(body) + "\n\n" + body + "\0");
        await TestHelpers.WaitUntil(() => applied == "secret-text");

        await engine.DisposeAsync();
    }

    [Fact]
    public async Task ErrorAndClose_InQuickSuccession_SchedulesSingleReconnect()
    {
        var factory = new TransportFactory();
        var engine = CreateEngine(() => factory.Create(), reconnectDelay: TimeSpan.FromMilliseconds(100));
        engine.Start();
        await WaitForConnectedAsync(factory.Last);

        await engine.OnErrorAsync(new InvalidOperationException("simulated error"));
        await engine.OnClosedAsync("remote close");
        for (var i = 0; i < 100 && factory.CreatedCount < 2; i++)
        {
            await Task.Delay(50);
        }
        if (factory.CreatedCount < 2)
        {
            Assert.Fail($"CreatedCount={factory.CreatedCount}, DisposedCount={factory.DisposedCount}, LastSent={factory.Last.Sent.Count}");
        }
        await Task.Delay(100);

        await engine.DisposeAsync();
        Assert.Equal(2, factory.CreatedCount);
        Assert.Equal(2, factory.DisposedCount);
    }

    [Fact]
    public async Task Reconnect_InFlight_DuplicateTrigger_IsDropped()
    {
        var factory = new TransportFactory();
        var engine = CreateEngine(() => factory.Create(), reconnectDelay: TimeSpan.FromSeconds(30));
        engine.Start();
        await WaitForConnectedAsync(factory.Last);

        await engine.OnErrorAsync(new InvalidOperationException("first"));
        await engine.OnErrorAsync(new InvalidOperationException("second"));
        await Task.Delay(100);

        Assert.Equal(1, factory.CreatedCount);
        await engine.DisposeAsync();
        await Task.Delay(50);
        Assert.Equal(1, factory.CreatedCount);
    }

    [Fact]
    public async Task Stop_AfterError_NoReconnectAttempted()
    {
        var factory = new TransportFactory();
        var engine = CreateEngine(() => factory.Create(), reconnectDelay: TimeSpan.Zero);
        engine.Start();
        await WaitForConnectedAsync(factory.Last);

        await engine.StopAsync();
        await engine.OnErrorAsync(new InvalidOperationException("simulated error"));
        await Task.Delay(100);

        Assert.Equal(1, factory.CreatedCount);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Stop_DisposesCts_PendingReconnect_ExitsQuietly()
    {
        Exception? unobserved = null;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            unobserved = args.Exception;
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var factory = new TransportFactory();
            var engine = CreateEngine(() => factory.Create(), reconnectDelay: TimeSpan.FromSeconds(30));
            engine.Start();
            await WaitForConnectedAsync(factory.Last);

            await engine.OnErrorAsync(new InvalidOperationException("simulated error"));
            await engine.StopAsync();
            await engine.DisposeAsync();

            for (var i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(50);
            }

            Assert.Null(unobserved);
            Assert.Equal(1, factory.CreatedCount);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task ConnectFailure_ClientDisposed()
    {
        var factory = new TransportFactory();
        var engine = CreateEngine(() => factory.Create(System.Net.HttpStatusCode.InternalServerError));
        engine.Start();

        await TestHelpers.WaitUntil(() => factory.CreatedCount == 1);
        await TestHelpers.WaitUntil(() => factory.DisposedCount == 1);
        await Task.Delay(50);

        Assert.Equal(1, factory.DisposedCount);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_SucceedsAfterTransientHandshakeFailure()
    {
        var factory = new TransportFactory(
            System.Net.HttpStatusCode.InternalServerError,
            null);
        var engine = CreateEngine(() => factory.Create(), reconnectDelay: TimeSpan.FromMilliseconds(20));
        engine.Start();

        // 第一次连接因 500 握手失败，应释放失败 transport 并进入退避重连
        await TestHelpers.WaitUntil(() => factory.CreatedCount == 2);
        await Task.Delay(100);

        // 第二次握手成功后应能发出 CONNECT 并回送 CONNECTED 完成订阅
        await TestHelpers.WaitUntil(() => factory.Last.Sent.Count >= 1);
        factory.Last.Enqueue("CONNECTED\n\n\0");
        await TestHelpers.WaitUntil(() => factory.Last.Sent.Count >= 2);

        await Task.Delay(50);
        Assert.Equal(2, factory.CreatedCount);
        await engine.DisposeAsync();
        Assert.Equal(2, factory.DisposedCount);
    }

    [Fact]
    public async Task Reconnect_UsesCookieTwice_ThenStaysInSessionRecoveryPhase()
    {
        var factory = new TransportFactory();
        var recoveryCalls = 0;
        using var recoveryStarted = new SemaphoreSlim(0);
        var engine = CreateEngine(
            () => factory.Create(System.Net.HttpStatusCode.InternalServerError),
            reconnectDelay: TimeSpan.FromMilliseconds(10),
            onSessionExpired: () =>
            {
                Interlocked.Increment(ref recoveryCalls);
                recoveryStarted.Release();
                return Task.CompletedTask;
            });
        engine.Start();

        await recoveryStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(80);

        Assert.Equal(3, factory.CreatedCount);
        Assert.True(recoveryCalls >= 1);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task SessionExpired_StopsAutoReconnect_AndNotifies()
    {
        var sessionExpiredCalls = 0;
        var statuses = new List<string>();
        var factory = new TransportFactory();
        var engine = CreateEngine(
            () => factory.Create(System.Net.HttpStatusCode.Unauthorized),
            onStatus: statuses.Add,
            onSessionExpired: () =>
            {
                Interlocked.Increment(ref sessionExpiredCalls);
                return Task.CompletedTask;
            });
        engine.Start();

        await TestHelpers.WaitUntil(() => sessionExpiredCalls == 1);
        await TestHelpers.WaitUntil(() => factory.DisposedCount == 1);
        await Task.Delay(100);

        Assert.Equal(1, factory.CreatedCount);
        Assert.Contains(statuses, static s =>
            s.Contains("Session expired", StringComparison.OrdinalIgnoreCase)
            || s.Contains("会话已过期", StringComparison.Ordinal));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task SendLocalText_AfterStop_CompletesWithoutUnobservedException()
    {
        var transport = new FakeWebSocketTransport();
        var engine = CreateEngine(() => transport);
        engine.Start();
        await WaitForConnectedAsync(transport);

        await engine.StopAsync();
        engine.SendLocalText("after-stop", UiText.ClipboardSource);
        await Task.Delay(100);

        await engine.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionChanged_Fires_OnConnect_And_Disconnect()
    {
        var events = new List<bool>();
        var transport = new FakeWebSocketTransport();
        var engine = CreateEngine(() => transport, onConnectionChanged: events.Add);
        engine.Start();
        await WaitForConnectedAsync(transport);

        Assert.Equal(new[] { true }, events);

        await engine.OnErrorAsync(new InvalidOperationException("simulated error"));

        Assert.Equal(new[] { true, false }, events);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task ClipboardRetry_FailsTwiceThenSucceeds_ReturnsTrue()
    {
        var attempts = 0;
        var ok = await TextSyncEngine.SetClipboardWithRetryAsync(
            "text",
            (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                return attempts < 3
                    ? Task.FromException(new ExternalException("locked"))
                    : Task.CompletedTask;
            },
            maxAttempts: 5,
            retryDelay: TimeSpan.FromMilliseconds(1));

        Assert.True(ok);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ClipboardRetry_AlwaysFails_ReturnsFalse_AfterMaxAttempts()
    {
        var attempts = 0;
        var ok = await TextSyncEngine.SetClipboardWithRetryAsync(
            "text",
            (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromException(new ExternalException("locked"));
            },
            maxAttempts: 4,
            retryDelay: TimeSpan.FromMilliseconds(1));

        Assert.False(ok);
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task ClipboardRetry_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TextSyncEngine.SetClipboardWithRetryAsync(
                "text",
                (_, _) =>
                {
                    Interlocked.Increment(ref attempts);
                    return Task.FromException(new ExternalException("locked"));
                },
                maxAttempts: 5,
                retryDelay: TimeSpan.FromMilliseconds(1),
                cancellationToken: cts.Token));

        Assert.Equal(1, attempts);
    }
}
