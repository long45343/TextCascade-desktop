using System.Diagnostics;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

// 从 v2 移植功能的回归测试：序列号轮询、短重试、发送超时、日志脱敏、
// ProtectedData、入站后台重试、唤醒打断退避、UU 远程规避。
public class PortedFeaturesTests
{
    // ---------- 决策 1：序列号轮询只读一次 ----------

    [Fact]
    public void ClipboardMonitor_PollWithUnchangedSequence_DoesNotReadOrNotify()
    {
        var readCount = 0;
        var notifyCount = 0;
        var seq = 100u;
        var monitor = new ClipboardMonitor(
            _ => notifyCount++,
            () => seq,
            () =>
            {
                readCount++;
                return "hello";
            });

        monitor.Start();
        var readsAfterStart = readCount;
        Assert.Equal(1, notifyCount);

        // 序列号未变：轮询不应触发任何读取
        monitor.OnPollTick();
        monitor.OnPollTick();
        Assert.Equal(readsAfterStart, readCount);
        Assert.Equal(1, notifyCount);

        monitor.Dispose();
    }

    [Fact]
    public void ClipboardMonitor_PollWithChangedSequence_ReadsAndNotifies()
    {
        var notifyCount = 0;
        var seq = 100u;
        var text = "hello";
        var monitor = new ClipboardMonitor(
            _ => notifyCount++,
            () => seq,
            () => text);

        monitor.Start();
        Assert.Equal(1, notifyCount);

        // 序列号变化 + 内容变化 → 读取并回调
        seq = 101;
        text = "world";
        monitor.OnPollTick();
        Assert.Equal(2, notifyCount);

        monitor.Dispose();
    }

    [Fact]
    public void ClipboardMonitor_SameContentWithNewSequence_SuppressedByHash()
    {
        var notifyCount = 0;
        var seq = 100u;
        var monitor = new ClipboardMonitor(
            _ => notifyCount++,
            () => seq,
            () => "same");

        monitor.Start();
        Assert.Equal(1, notifyCount);

        // 序列号变了但内容相同：hash 去重应挡住重复回调
        seq = 101;
        monitor.OnPollTick();
        Assert.Equal(1, notifyCount);

        monitor.Dispose();
    }

    // ---------- 决策 1：读取失败不消费序号 ----------

    [Fact]
    public async Task ClipboardMonitor_ReadFailure_DoesNotConsumeSequenceAndRetries()
    {
        var attempts = 0;
        var seq = 100u;
        var monitor = new ClipboardMonitor(
            _ => { },
            () => seq,
            () =>
            {
                attempts++;
                // 前两次模拟剪贴板被独占，第三次成功
                if (attempts <= 2)
                {
                    throw new System.Runtime.InteropServices.ExternalException("clipboard locked");
                }
                return "recovered";
            })
        {
            RetryDelay = TimeSpan.Zero,
            RetryAttempts = 3
        };

        monitor.Start();
        await TestHelpers.WaitUntil(() => attempts >= 3);
        monitor.Dispose();

        // 失败后未消费序号，重试路径得以再次读取
        Assert.True(attempts >= 3);
    }

    // ---------- 决策 4（关联）：防抖延时 ----------

    [Fact]
    public void ClipboardMonitor_WithReadDelay_DefersReadUntilDelayTick()
    {
        var notifyCount = 0;
        var monitor = new ClipboardMonitor(
            _ => notifyCount++,
            () => 100u,
            () => "hello",
            () => TimeSpan.FromMilliseconds(200));

        monitor.Start();
        // 有延时：Start 的立即读取被推迟，尚未回调
        Assert.Equal(0, notifyCount);
        Assert.True(monitor.IsDelayPending);

        monitor.TriggerDelayTickForTest();
        Assert.Equal(1, notifyCount);

        monitor.Dispose();
    }

    // ---------- 决策 5：发送超时后 Abort ----------

    [Fact]
    public async Task StompClient_SendTimeout_AbortsConnection()
    {
        var transport = new BlockingSendTransport();
        var listener = new RecordingStompListener([]);
        var client = new StompClient(
            "ws://localhost:8080/clipsocket",
            string.Empty,
            listener,
            () => transport,
            new ManualTimeProvider());

        // 握手期间允许发送，先建立连接
        transport.BlockSends = false;
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connectTask = client.ConnectAsync(connectCts.Token);
        transport.Enqueue("CONNECTED\n\n\0");
        await connectTask;
        await TestHelpers.WaitUntil(() => listener.ConnectedCount > 0);

        // 此后所有发送都会挂起，触发发送超时（实例接缝，避免污染并行测试）
        transport.BlockSends = true;
        client.SendTimeout = TimeSpan.FromMilliseconds(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SendAsync("/app/cliptext", "body", CancellationToken.None));

        // 超时必须判死：Abort 被调用，让接收循环走既有错误路径重连
        Assert.True(transport.AbortCount >= 1);
        await client.DisposeAsync();
    }

    // ---------- 决策 9：唤醒打断退避 ----------

    [Fact]
    public async Task Engine_NotifyWake_InterruptsReconnectBackoff()
    {
        var factory = new CountingTransportFactory();
        var engine = new TextSyncEngine(
            BuildConfig(),
            new TestSynchronizationContext(),
            _ => { },
            _ => { },
            transportFactory: () => factory.Create(),
            timeProvider: new ManualTimeProvider());
        // 先让引擎进入退避：给一个极长的重连延时
        engine.ReconnectDelayOverride = TimeSpan.FromMinutes(5);

        engine.Start();
        var first = factory.Last;
        first.Enqueue("CONNECTED\n\n\0");
        await TestHelpers.WaitUntil(() => first.Sent.Count >= 2);

        // 显式触发断线回调进入退避等待（5 分钟）。
        // 不用 transport.Abort() 模拟：fake 传输的接收循环阻塞在信号量上，
        // Abort 并不能保证唤醒它，能否走到 OnClosedAsync 取决于时序，会造成偶发失败。
        await engine.OnClosedAsync("test disconnect");
        await Task.Delay(100);

        var createdBeforeWake = factory.CreatedCount;
        engine.NotifyWake();

        // 唤醒后约 1 秒即重连，无需等 5 分钟
        await TestHelpers.WaitUntil(() => factory.CreatedCount > createdBeforeWake, timeoutMs: 8000);
        await engine.DisposeAsync();
    }

    // ---------- 决策 4：UU 规避监控 ----------

    [Fact]
    public void EvasionMonitor_ForegroundMatch_TriggersEvasion()
    {
        var states = new List<bool>();
        var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: states.Add,
            foregroundProcessNameProvider: () => "GameViewer",
            processRunningChecker: _ => true,
            timeProvider: new ManualTimeProvider());

        monitor.Start();
        Assert.True(monitor.IsInEvasion);
        Assert.True(monitor.IsProcessRunning);
        Assert.Contains(true, states);
        monitor.Dispose();
    }

    [Fact]
    public void EvasionMonitor_ForegroundMatchIsCaseInsensitive()
    {
        var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: _ => { },
            foregroundProcessNameProvider: () => "gameviewer",
            processRunningChecker: _ => true,
            timeProvider: new ManualTimeProvider());

        monitor.Start();
        Assert.True(monitor.IsInEvasion);
        monitor.Dispose();
    }

    [Fact]
    public void EvasionMonitor_BackgroundRunning_DoesNotTriggerEvasionButMarksRunning()
    {
        var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: _ => { },
            foregroundProcessNameProvider: () => "explorer",
            processRunningChecker: _ => true,
            timeProvider: new ManualTimeProvider());

        monitor.Start();
        // 后台运行：不挂起，但应标记进程存活以启用 200ms 延时
        Assert.False(monitor.IsInEvasion);
        Assert.True(monitor.IsProcessRunning);
        monitor.Dispose();
    }

    [Fact]
    public void EvasionMonitor_CustomProcessNames_AreMatched()
    {
        var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => ["MyRemoteTool"],
            onEvasionStateChanged: _ => { },
            foregroundProcessNameProvider: () => "MyRemoteTool",
            processRunningChecker: _ => true,
            timeProvider: new ManualTimeProvider());

        monitor.Start();
        Assert.True(monitor.IsInEvasion);
        monitor.Dispose();
    }

    [Fact]
    public void EvasionMonitor_Disabled_NeverEvades()
    {
        var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => false,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: _ => { },
            foregroundProcessNameProvider: () => "GameViewer",
            processRunningChecker: _ => true,
            timeProvider: new ManualTimeProvider());

        monitor.Start();
        Assert.False(monitor.IsInEvasion);
        Assert.False(monitor.IsProcessRunning);
        monitor.Dispose();
    }

    [Fact]
    public void EvasionMonitor_SwitchAwayFromForeground_Resumes()
    {
        var foreground = "GameViewer";
        var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: _ => { },
            foregroundProcessNameProvider: () => foreground,
            processRunningChecker: _ => true,
            timeProvider: new ManualTimeProvider());

        monitor.Start();
        Assert.True(monitor.IsInEvasion);

        foreground = "explorer";
        monitor.CheckNow();
        Assert.False(monitor.IsInEvasion);
        // 进程仍在后台运行，延时机制仍需生效
        Assert.True(monitor.IsProcessRunning);
        monitor.Dispose();
    }

    [Fact]
    public void EvasionMonitor_EmptyForegroundWindow_DoesNotEvade()
    {
        var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: _ => { },
            foregroundProcessNameProvider: () => null,
            processRunningChecker: _ => false,
            timeProvider: new ManualTimeProvider());

        monitor.Start();
        Assert.False(monitor.IsInEvasion);
        monitor.Dispose();
    }

    [Fact]
    public void EvasionMonitor_StopForcesResume()
    {
        var states = new List<bool>();
        var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: states.Add,
            foregroundProcessNameProvider: () => "GameViewer",
            processRunningChecker: _ => true,
            timeProvider: new ManualTimeProvider());

        monitor.Start();
        Assert.True(monitor.IsInEvasion);

        monitor.Stop();
        Assert.False(monitor.IsInEvasion);
        Assert.False(monitor.IsProcessRunning);
        // 最后一次回调必须是恢复
        Assert.False(states[^1]);
        monitor.Dispose();
    }

    // ---------- 配置项 ----------

    [Fact]
    public void SettingsData_DefaultEvasionDelayIs200ms()
    {
        var data = new SettingsData();
        Assert.Equal(200, data.UuEvasionDelayMs);
        Assert.Equal(200, SettingsData.DefaultUuEvasionDelayMs);
    }

    [Fact]
    public void SettingsData_DefaultProcessNames_ContainActualGameViewerBinaries()
    {
        Assert.Contains("GameViewer", SettingsData.DefaultUuProcessNames);
        Assert.Contains("GameViewerLauncher", SettingsData.DefaultUuProcessNames);
        Assert.Contains("GameViewerServer", SettingsData.DefaultUuProcessNames);
    }

    [Fact]
    public void SettingsStore_ZeroEvasionDelay_NormalizedToDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TextCascadePortTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"uu_evasion_delay_ms\": 0}");
            var store = SettingsStore.LoadFromPath(path);
            Assert.Equal(SettingsData.DefaultUuEvasionDelayMs, store.Data.UuEvasionDelayMs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SettingsStore_EvasionKeysRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TextCascadePortTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = SettingsStore.LoadFromPath(path);
            store.Data.UuEvasionEnabled = true;
            store.Data.UuEvasionDelayMs = 350;
            store.Data.UuEvasionProcessNames = ["Foo"];
            store.Save();

            var reloaded = SettingsStore.LoadFromPath(path);
            Assert.True(reloaded.Data.UuEvasionEnabled);
            Assert.Equal(350, reloaded.Data.UuEvasionDelayMs);
            Assert.Equal(["Foo"], reloaded.Data.UuEvasionProcessNames);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- 辅助 ----------

    private static ClipConfig BuildConfig()
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

    // 可阻塞发送的传输，用于验证发送超时
    private sealed class BlockingSendTransport : Core.IWebSocketTransport
    {
        private readonly Queue<byte[]> _pending = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly object _gate = new();

        public System.Net.WebSockets.WebSocketState State { get; private set; }

        public System.Net.HttpStatusCode? LastHttpStatusCode => null;

        public bool BlockSends { get; set; }

        public int AbortCount { get; private set; }

        public void Enqueue(string text)
        {
            lock (_gate)
            {
                _pending.Enqueue(System.Text.Encoding.UTF8.GetBytes(text));
                _signal.Release();
            }
        }

        public Task ConnectAsync(Uri uri, string? cookieHeader, CancellationToken cancellationToken)
        {
            State = System.Net.WebSockets.WebSocketState.Open;
            return Task.CompletedTask;
        }

        public async Task SendAsync(
            ReadOnlyMemory<byte> payload,
            System.Net.WebSockets.WebSocketMessageType type,
            System.Net.WebSockets.WebSocketMessageFlags flags,
            CancellationToken cancellationToken)
        {
            if (BlockSends)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public async ValueTask<System.Net.WebSockets.ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                byte[]? item = null;
                lock (_gate)
                {
                    if (_pending.Count > 0)
                    {
                        item = _pending.Dequeue();
                    }
                }
                if (item is not null)
                {
                    var count = Math.Min(item.Length, buffer.Length);
                    item.AsSpan(0, count).CopyTo(buffer.Span);
                    return new System.Net.WebSockets.ValueWebSocketReceiveResult(
                        count, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true);
                }
                await _signal.WaitAsync(cancellationToken);
            }
        }

        public Task CloseAsync(
            System.Net.WebSockets.WebSocketCloseStatus status,
            string? description,
            CancellationToken cancellationToken)
        {
            State = System.Net.WebSockets.WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public void Abort()
        {
            AbortCount++;
            State = System.Net.WebSockets.WebSocketState.Aborted;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingStompListener : Core.IStompListener
    {
        private readonly List<string> _events;

        public RecordingStompListener(List<string> events)
        {
            _events = events;
        }

        public int ConnectedCount { get; private set; }

        public Task OnConnectedAsync()
        {
            ConnectedCount++;
            _events.Add("connected");
            return Task.CompletedTask;
        }

        public Task OnMessageAsync(string body) => Task.CompletedTask;

        public Task OnClosedAsync(string reason)
        {
            _events.Add("closed");
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(Exception error)
        {
            _events.Add("error");
            return Task.CompletedTask;
        }
    }

    private sealed class CountingTransportFactory
    {
        private readonly object _gate = new();
        private readonly List<FakeWebSocketTransport> _created = new();

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
            var transport = new FakeWebSocketTransport();
            lock (_gate)
            {
                _created.Add(transport);
            }
            return transport;
        }
    }
}
