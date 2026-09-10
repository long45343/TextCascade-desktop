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

    [Fact]
    public void Enabled_ForegroundMatchesUu_EntersEvasion()
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
}
