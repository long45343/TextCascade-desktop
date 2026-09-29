using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

public class UuRemoteEvasionMonitorTests
{
    [Fact]
    public void Disabled_DoesNotTriggerEvasion_EvenWhenForegroundMatches()
    {
        var changes = new List<bool>();
        var currentForeground = "UURemote";

        using var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => false,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: changes.Add,
            foregroundProcessNameProvider: () => currentForeground);

        monitor.Start();
        monitor.CheckNow();

        Assert.False(monitor.IsInEvasion);
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData("GameViewer")]
    [InlineData("gameviewer")]
    [InlineData("GameViewerLauncher")]
    [InlineData("GameViewerServer")]
    [InlineData("uuyc-cli")]
    [InlineData("UURemote")]
    public void Enabled_ForegroundMatchesUu_EntersEvasion(string foregroundProcess)
    {
        var changes = new List<bool>();
        var currentForeground = foregroundProcess;

        using var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: changes.Add,
            foregroundProcessNameProvider: () => currentForeground);

        monitor.Start();

        Assert.True(monitor.IsInEvasion);
        Assert.Single(changes);
        Assert.True(changes[0]);
    }

    [Fact]
    public void Enabled_CaseInsensitiveMatching_Succeeds()
    {
        var changes = new List<bool>();
        var currentForeground = "uuremotedesktop";

        using var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: changes.Add,
            foregroundProcessNameProvider: () => currentForeground);

        monitor.Start();

        Assert.True(monitor.IsInEvasion);
        Assert.Single(changes);
        Assert.True(changes[0]);
    }

    [Fact]
    public void ForegroundSwitchesAwayFromUu_ExitsEvasion()
    {
        var changes = new List<bool>();
        var currentForeground = "UURemote";

        using var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: changes.Add,
            foregroundProcessNameProvider: () => currentForeground);

        monitor.Start();
        Assert.True(monitor.IsInEvasion);

        // 切换至其他前台应用（如 Chrome）
        currentForeground = "chrome";
        monitor.CheckNow();

        Assert.False(monitor.IsInEvasion);
        Assert.Equal(2, changes.Count);
        Assert.True(changes[0]);
        Assert.False(changes[1]);
    }

    [Fact]
    public void InTrayOrBackground_ForegroundIsOtherApp_DoesNotTriggerEvasion()
    {
        var changes = new List<bool>();
        // UU 在托盘常驻，用户当前操作的是记事本
        var currentForeground = "notepad";

        using var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: changes.Add,
            foregroundProcessNameProvider: () => currentForeground);

        monitor.Start();

        Assert.False(monitor.IsInEvasion);
        Assert.Empty(changes);
    }

    [Fact]
    public void ForegroundNullOrEmpty_DoesNotTriggerEvasion()
    {
        var changes = new List<bool>();
        string? currentForeground = null;

        using var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: changes.Add,
            foregroundProcessNameProvider: () => currentForeground);

        monitor.Start();

        Assert.False(monitor.IsInEvasion);
        Assert.Empty(changes);
    }

    [Fact]
    public void CustomProcessNames_MatchesCorrectly()
    {
        var changes = new List<bool>();
        var currentForeground = "SpecialRemoteTool";
        var customList = new List<string> { "SpecialRemoteTool", "AnotherTool" };

        using var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => customList,
            onEvasionStateChanged: changes.Add,
            foregroundProcessNameProvider: () => currentForeground);

        monitor.Start();

        Assert.True(monitor.IsInEvasion);
        Assert.Single(changes);
        Assert.True(changes[0]);
    }

    [Fact]
    public void StopOrDisposeWhileInEvasion_RestoresStateImmediately()
    {
        var changes = new List<bool>();
        var currentForeground = "UURemote";

        var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: changes.Add,
            foregroundProcessNameProvider: () => currentForeground);

        monitor.Start();
        Assert.True(monitor.IsInEvasion);

        monitor.Stop();
        Assert.False(monitor.IsInEvasion);
        Assert.Equal(2, changes.Count);
        Assert.False(changes[1]);

        monitor.Dispose();
        Assert.False(monitor.IsInEvasion);
    }

    [Fact]
    public async Task TextSyncEngine_WhenEvasionPaused_DropsOutboundAndInbound()
    {
        var config = new ClipConfig(
            "https://your-server:8443",
            "tok",
            null,
            "alice",
            "uuid-1",
            "PC",
            0,
            ClipConfig.DefaultMaxTextBytes,
            10,
            25,
            30,
            ClipConfig.DefaultHashRounds,
            "salt",
            "",
            CipherEnabled: false,
            TrustAllCertificates: false,
            ServerCertificateThumbprint: "",
            RelaunchOnBoot: false,
            WebsocketStatusNotification: false,
            LocalMaxClipboardBytes: ClipConfig.DefaultMaxTextBytes);

        var clipboardWrites = new List<string>();
        var fakeBridge = new ClipboardBridge(
            new TestSynchronizationContext(),
            setOverride: (text, _) =>
            {
                clipboardWrites.Add(text);
                return Task.CompletedTask;
            });

        await using var engine = new TextSyncEngine(
            config,
            new TestSynchronizationContext(),
            onStatus: _ => { },
            onRemoteTextApplied: _ => { },
            transportFactory: () => new FakeWebSocketTransport(),
            clipboard: fakeBridge);

        engine.Start();

        // 模拟进入规避状态
        engine.SetEvasionPaused(true);
        Assert.True(engine.IsEvasionPaused);

        // 出站发送：规避中被忽略
        engine.SendLocalText("local-data", "clipboard");

        // 入站剪贴板应用：规避中被忽略
        var inboundClip = new InboundClipMessage(
            Version: 1,
            Payload: "remote-data",
            Encrypted: false,
            Hash: HashUtil.Fnv1A64Hex("remote-data"),
            FromClientId: "uuid-2");
        await engine.OnClipAsync(inboundClip);

        Assert.Empty(clipboardWrites);

        // 解除规避状态
        engine.SetEvasionPaused(false);
        Assert.False(engine.IsEvasionPaused);

        // 恢复后正常接收
        var secondInboundClip = new InboundClipMessage(
            Version: 2,
            Payload: "remote-data-2",
            Encrypted: false,
            Hash: HashUtil.Fnv1A64Hex("remote-data-2"),
            FromClientId: "uuid-2");
        await engine.OnClipAsync(secondInboundClip);

        Assert.Single(clipboardWrites);
        Assert.Equal("remote-data-2", clipboardWrites[0]);
    }

    [Fact]
    public void ProcessRunningInBackground_SetsIsProcessRunningTrue_AndIsInEvasionFalse()
    {
        var evasionChanges = new List<bool>();
        var runningChanges = new List<bool>();
        var currentForeground = "notepad";
        var isTargetRunning = true;

        using var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: evasionChanges.Add,
            foregroundProcessNameProvider: () => currentForeground,
            processRunningChecker: _ => isTargetRunning,
            onProcessRunningChanged: runningChanges.Add);

        monitor.Start();

        // 后台运行，前台是记事本：延时标志为 true，但前台完全挂起为 false
        Assert.False(monitor.IsInEvasion);
        Assert.True(monitor.IsProcessRunning);
        Assert.Empty(evasionChanges);
        Assert.Single(runningChanges);
        Assert.True(runningChanges[0]);

        // 目标进程退出
        isTargetRunning = false;
        monitor.CheckNow();

        Assert.False(monitor.IsInEvasion);
        Assert.False(monitor.IsProcessRunning);
        Assert.Equal(2, runningChanges.Count);
        Assert.False(runningChanges[1]);
    }

    [Fact]
    public void ForegroundMatches_SetsBothIsProcessRunningAndIsInEvasionTrue()
    {
        var evasionChanges = new List<bool>();
        var runningChanges = new List<bool>();
        var currentForeground = "GameViewer";
        var checkerCalled = false;

        using var monitor = new UuRemoteEvasionMonitor(
            isEnabled: () => true,
            getTargetProcesses: () => SettingsData.DefaultUuProcessNames,
            onEvasionStateChanged: evasionChanges.Add,
            foregroundProcessNameProvider: () => currentForeground,
            processRunningChecker: _ =>
            {
                checkerCalled = true;
                return true;
            },
            onProcessRunningChanged: runningChanges.Add);

        monitor.Start();

        // 前台命中：两者均为 true，且由于前台已命中，无需扫描后台进程
        Assert.True(monitor.IsInEvasion);
        Assert.True(monitor.IsProcessRunning);
        Assert.False(checkerCalled);
        Assert.Single(evasionChanges);
        Assert.Single(runningChanges);
    }

    [Fact]
    public async Task TextSyncEngine_WhenEvasionDelayEnabled_DelaysInboundWrite_AndAbortsIfPausedDuringDelay()
    {
        var config = new ClipConfig(
            "https://your-server:8443",
            "tok",
            null,
            "alice",
            "uuid-1",
            "PC",
            0,
            ClipConfig.DefaultMaxTextBytes,
            10,
            25,
            30,
            ClipConfig.DefaultHashRounds,
            "salt",
            "",
            CipherEnabled: false,
            TrustAllCertificates: false,
            ServerCertificateThumbprint: "",
            RelaunchOnBoot: false,
            WebsocketStatusNotification: false,
            LocalMaxClipboardBytes: ClipConfig.DefaultMaxTextBytes);

        var clipboardWrites = new List<string>();
        var fakeBridge = new ClipboardBridge(
            new TestSynchronizationContext(),
            setOverride: (text, _) =>
            {
                clipboardWrites.Add(text);
                return Task.CompletedTask;
            });

        var delay = TimeSpan.FromMilliseconds(50);
        await using var engine = new TextSyncEngine(
            config,
            new TestSynchronizationContext(),
            onStatus: _ => { },
            onRemoteTextApplied: _ => { },
            transportFactory: () => new FakeWebSocketTransport(),
            clipboard: fakeBridge,
            getEvasionDelay: () => delay);

        engine.Start();

        // 1. 正常延时写入
        var inboundClip = new InboundClipMessage(
            Version: 1,
            Payload: "delayed-text",
            Encrypted: false,
            Hash: HashUtil.Fnv1A64Hex("delayed-text"),
            FromClientId: "uuid-2");

        var applyTask = engine.OnClipAsync(inboundClip);
        // 延时期间尚未写入
        Assert.Empty(clipboardWrites);
        await applyTask;
        // 延时结束后写入完成
        Assert.Single(clipboardWrites);
        Assert.Equal("delayed-text", clipboardWrites[0]);

        // 2. 延时期间前台激活了 UU 远程触发挂起 -> 延时结束后不写入
        var secondClip = new InboundClipMessage(
            Version: 2,
            Payload: "delayed-aborted-text",
            Encrypted: false,
            Hash: HashUtil.Fnv1A64Hex("delayed-aborted-text"),
            FromClientId: "uuid-2");

        var abortedTask = engine.OnClipAsync(secondClip);
        // 在 50ms 延时中途前台切入 UU 远程
        engine.SetEvasionPaused(true);
        await abortedTask;

        // 写入次数依然为 1，新的未被写入
        Assert.Single(clipboardWrites);
    }

    [Fact]
    public void ClipboardMonitor_WhenReadDelayEnabled_DelaysReadUntilTimerTicks()
    {
        var notifications = new List<string>();
        var currentSequence = 100u;
        var clipboardText = "initial text";
        var delay = TimeSpan.FromMilliseconds(200);

        using var monitor = new ClipboardMonitor(
            onClipboardChanged: notifications.Add,
            getSequenceNumberOverride: () => currentSequence,
            getTextOverride: () => clipboardText,
            getReadDelay: () => delay);

        monitor.Start();
        // Start 会触发 ReadAndNotify，由于 delay > 0，启动了 delayTimer
        Assert.True(monitor.IsDelayPending);
        Assert.Empty(notifications);

        // 模拟延时到期
        monitor.TriggerDelayTickForTest();
        Assert.False(monitor.IsDelayPending);
        Assert.Single(notifications);
        Assert.Equal("initial text", notifications[0]);

        // 序号变化再次触发
        currentSequence = 101u;
        clipboardText = "second text";
        monitor.OnPollTick();

        Assert.True(monitor.IsDelayPending);
        Assert.Single(notifications);

        // 延时触发前再次变动（防抖重置）
        currentSequence = 102u;
        clipboardText = "third text";
        monitor.OnPollTick();
        Assert.True(monitor.IsDelayPending);

        monitor.TriggerDelayTickForTest();
        Assert.False(monitor.IsDelayPending);
        Assert.Equal(2, notifications.Count);
        Assert.Equal("third text", notifications[1]);
    }
}
