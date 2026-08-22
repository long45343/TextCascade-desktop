using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

public class OptimizationDecisionsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalLogPath;

    public OptimizationDecisionsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OptDecisionsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _originalLogPath = Logger.LogPath;
    }

    public void Dispose()
    {
        Logger.LogPath = _originalLogPath;
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    // ==========================================
    // 决策 1：证书指纹全链路透传与持久化
    // ==========================================

    [Fact]
    public void Decision1_SettingsStore_NormalizeThumbprint_NormalizesCorrectly()
    {
        Assert.Equal("11223344AABB", SettingsStore.NormalizeThumbprint("  11:22:33:44:aa:bb  "));
        Assert.Equal("AABBCCDD", SettingsStore.NormalizeThumbprint("aa:bb:cc:dd"));
        Assert.Equal("AABBCCDD", SettingsStore.NormalizeThumbprint("  AABBCCDD  "));
        Assert.Equal(string.Empty, SettingsStore.NormalizeThumbprint(null));
        Assert.Equal(string.Empty, SettingsStore.NormalizeThumbprint("   "));
        Assert.Equal(string.Empty, SettingsStore.NormalizeThumbprint(""));
    }

    [Fact]
    public void Decision1_SettingsStore_NormalizeData_AppliesToData()
    {
        var data = new SettingsData
        {
            ServerCertificateThumbprint = "  ff:ee:dd:cc:bb:aa  "
        };
        SettingsStore.NormalizeData(data);
        Assert.Equal("FFEEDDCCBBAA", data.ServerCertificateThumbprint);
    }

    [Fact]
    public void Decision1_ClipConfig_FromSettings_DataOverload_MapsThumbprint()
    {
        var data = new SettingsData
        {
            ServerUrl = "https://server.test:8443",
            ServerCertificateThumbprint = "11223344AABB",
            TrustAllCertificates = true
        };
        var config = ClipConfig.FromSettings(data);
        Assert.Equal("11223344AABB", config.ServerCertificateThumbprint);
        Assert.True(config.TrustAllCertificates);
    }

    [Fact]
    public async Task Decision1_SyncClient_ConnectAsync_PassesThumbprintToTransport()
    {
        var fakeTransport = new FakeWebSocketTransport();
        var config = new ClipConfig(
            ServerUrl: "https://server.test:8443",
            AuthToken: "test_token",
            TokenExpiresAtUtc: DateTime.UtcNow.AddHours(1),
            Username: "alice",
            ClientId: "client_1",
            ClientName: "PC",
            LastServerVersion: 0,
            MaxTextBytes: 512000,
            HelloTimeoutSeconds: 5,
            HeartbeatIntervalSeconds: 30,
            HeartbeatTimeoutSeconds: 60,
            HashRounds: 664937,
            Salt: "salt",
            DerivedKeyBase64: "key",
            CipherEnabled: true,
            TrustAllCertificates: true,
            ServerCertificateThumbprint: "11223344AABB",
            RelaunchOnBoot: false,
            WebsocketStatusNotification: false,
            LocalMaxClipboardBytes: 512000);

        var listener = new TestSyncListener();
        await using var client = new SyncClient(config, "test_token", listener, () => fakeTransport);

        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal("11223344AABB", fakeTransport.LastServerCertificateThumbprint);
        Assert.True(fakeTransport.LastTrustAllCertificates);
    }

    [Fact]
    public void Decision1_LoginRequest_DefaultsAndExplicitThumbprint()
    {
        var reqDefault = new LoginRequest("https://srv", "u", "p", 100, "s", true);
        Assert.Equal(string.Empty, reqDefault.ServerCertificateThumbprint);

        var reqExplicit = new LoginRequest("https://srv", "u", "p", 100, "s", true, "11223344AABB");
        Assert.Equal("11223344AABB", reqExplicit.ServerCertificateThumbprint);
    }

    // ==========================================
    // 决策 2：CI Pull Request 触发器
    // ==========================================

    [Fact]
    public void Decision2_CiYaml_PullRequestAndPushOnlyTargetsV2()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var ciYmlPath = Path.Combine(workspaceRoot, ".github", "workflows", "ci.yml");
        if (!File.Exists(ciYmlPath))
        {
            ciYmlPath = Path.Combine(Directory.GetCurrentDirectory(), ".github", "workflows", "ci.yml");
        }

        Assert.True(File.Exists(ciYmlPath), $"ci.yml not found at {ciYmlPath}");
        var text = File.ReadAllText(ciYmlPath);

        Assert.Contains("push:", text);
        Assert.Contains("pull_request:", text);
        Assert.DoesNotContain("pull_request: {v2}", text);
        Assert.DoesNotContain("pull_request: {}", text);

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var inPush = false;
        var inPr = false;
        var pushBranches = new List<string>();
        var prBranches = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line == "push:")
            {
                inPush = true;
                inPr = false;
                continue;
            }
            if (line == "pull_request:")
            {
                inPr = true;
                inPush = false;
                continue;
            }
            if (line.StartsWith("jobs:") || line.StartsWith("name:"))
            {
                inPush = false;
                inPr = false;
                continue;
            }

            if (inPush && line.StartsWith("branches:"))
            {
                pushBranches.Add(line);
            }
            if (inPr && line.StartsWith("branches:"))
            {
                prBranches.Add(line);
            }
        }

        Assert.Single(pushBranches);
        Assert.Contains("v2", pushBranches[0]);
        Assert.Single(prBranches);
        Assert.Contains("v2", prBranches[0]);
    }

    // ==========================================
    // 决策 3：剪贴板写入兜底
    // ==========================================

    [Fact]
    public async Task Decision3_TryClipboardFallback_WritesMultilingualTextOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var clipPath = Path.Combine(Environment.SystemDirectory, "clip.exe");
        if (!File.Exists(clipPath))
        {
            return;
        }

        var bridge = new ClipboardBridge(
            new TestSynchronizationContext(),
            setOverride: static (_, _) => throw new ExternalException("simulate winforms failure"),
            getOverride: static () => string.Empty);

        var testContent = "TestMultilingual_文本同步_日本語_Emoji🎉_" + Guid.NewGuid().ToString("N");
        var written = await bridge.TryWriteTextAsync(testContent, CancellationToken.None);

        Assert.True(written);
    }

    // ==========================================
    // 决策 4：剪贴板监听低开销轮询
    // ==========================================

    [Fact]
    public void Decision4_ClipboardMonitor_PollWithUnchangedSequence_DoesNotReadOrNotify()
    {
        var notifyCount = 0;
        var readCount = 0;
        uint sequence = 100;

        using var monitor = new ClipboardMonitor(
            onClipboardChanged: _ => notifyCount++,
            getSequenceNumberOverride: () => sequence,
            getTextOverride: () =>
            {
                readCount++;
                return "hello";
            });

        monitor.Start();
        // Start immediately triggers one read
        var initialReads = readCount;

        // Poll with same sequence number
        monitor.OnPollTick();
        monitor.OnPollTick();
        monitor.OnPollTick();

        Assert.Equal(initialReads, readCount);
        Assert.Equal(1, notifyCount);
    }

    [Fact]
    public void Decision4_ClipboardMonitor_PollWithChangedSequence_TriggersReadAndNotify()
    {
        var notifyCount = 0;
        var readCount = 0;
        uint sequence = 100;
        var currentText = "initial text";

        using var monitor = new ClipboardMonitor(
            onClipboardChanged: _ => notifyCount++,
            getSequenceNumberOverride: () => sequence,
            getTextOverride: () =>
            {
                readCount++;
                return currentText;
            });

        monitor.Start();
        Assert.Equal(1, notifyCount);

        // Sequence unchanged -> no notify
        monitor.OnPollTick();
        Assert.Equal(1, notifyCount);

        // Sequence changed and new text -> triggers notify
        sequence = 101;
        currentText = "updated text";
        monitor.OnPollTick();
        Assert.Equal(2, notifyCount);
    }

    [Fact]
    public void Decision4_ClipboardMonitor_SequenceWrapAround_TriggersReadAndNotify()
    {
        var notifiedTexts = new List<string>();
        uint sequence = uint.MaxValue;
        var currentText = "text-before-wrap";

        using var monitor = new ClipboardMonitor(
            onClipboardChanged: t => notifiedTexts.Add(t),
            getSequenceNumberOverride: () => sequence,
            getTextOverride: () => currentText);

        monitor.Start();
        Assert.Single(notifiedTexts);

        // Sequence wraps around to 0
        sequence = 0;
        currentText = "text-after-wrap";
        monitor.OnPollTick();

        Assert.Equal(2, notifiedTexts.Count);
        Assert.Equal("text-after-wrap", notifiedTexts[1]);
    }

    // ==========================================
    // 决策 5：Logger 同步 I/O 缓存与轮转
    // ==========================================

    [Fact]
    public void Decision5_Logger_ConsecutiveWrites_AppendWithoutError()
    {
        var logFile = Path.Combine(_tempDir, "consecutive.log");
        Logger.LogPath = logFile;

        for (var i = 0; i < 20; i++)
        {
            Logger.Log($"Log entry {i}");
        }

        Assert.True(File.Exists(logFile));
        var lines = File.ReadAllLines(logFile);
        Assert.Equal(20, lines.Length);
    }

    [Fact]
    public void Decision5_Logger_Exceeds1MB_RotatesAndResets()
    {
        var logFile = Path.Combine(_tempDir, "rotate_test.log");
        Logger.LogPath = logFile;

        // Pre-fill file over 1MB
        File.WriteAllText(logFile, new string('A', 1024 * 1024 + 100));

        // Next log triggers rotation
        Logger.Log("New entry after rotation");

        var oldLog = Path.Combine(_tempDir, "TextCascade.old.log");
        Assert.True(File.Exists(oldLog));
        Assert.True(File.Exists(logFile));

        var currentLength = new FileInfo(logFile).Length;
        var oldLength = new FileInfo(oldLog).Length;

        Assert.True(oldLength >= 1024 * 1024);
        Assert.True(currentLength < 1024);
    }

    [Fact]
    public void Decision5_Logger_WriteFailure_SwallowsAndRecovers()
    {
        var invalidDirLog = Path.Combine(_tempDir, "invalid_dir", "dummy.txt", "TextCascade.log");
        File.WriteAllText(Path.Combine(_tempDir, "invalid_dir"), "not a directory");

        Logger.LogPath = invalidDirLog;

        // Should not throw
        Logger.Log("Should be swallowed");
        Logger.LogError("Should also be swallowed");

        // Recover to a valid log path
        var validLog = Path.Combine(_tempDir, "valid.log");
        Logger.LogPath = validLog;
        Logger.Log("Recovered successfully");

        Assert.True(File.Exists(validLog));
        Assert.Contains("Recovered successfully", File.ReadAllText(validLog));
    }

    // ==========================================
    // 决策 6：StartupManager 解耦 WinForms
    // ==========================================

    [Fact]
    public void Decision6_StartupManager_DoesNotReferenceWinForms()
    {
        var startupManagerType = typeof(StartupManager);
        var methods = startupManagerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        foreach (var m in methods)
        {
            var returnType = m.ReturnType;
            Assert.DoesNotContain("System.Windows.Forms", returnType.FullName ?? "");
            foreach (var p in m.GetParameters())
            {
                Assert.DoesNotContain("System.Windows.Forms", p.ParameterType.FullName ?? "");
            }
        }
    }

    [Fact]
    public void Decision6_CoreDirectory_OnlyClipboardFilesReferenceWinForms()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var coreDir = Path.Combine(workspaceRoot, "src", "Core");
        if (!Directory.Exists(coreDir))
        {
            coreDir = Path.Combine(Directory.GetCurrentDirectory(), "src", "Core");
        }

        Assert.True(Directory.Exists(coreDir), $"src/Core not found at {coreDir}");
        var csFiles = Directory.GetFiles(coreDir, "*.cs", SearchOption.AllDirectories);

        var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ClipboardBridge.cs",
            "ClipboardMonitor.cs"
        };

        foreach (var file in csFiles)
        {
            var fileName = Path.GetFileName(file);
            var content = File.ReadAllText(file);
            if (content.Contains("System.Windows.Forms"))
            {
                Assert.True(allowedFiles.Contains(fileName),
                    $"File {fileName} in src/Core contains reference to System.Windows.Forms, which violates Decision 6!");
            }
        }
    }

    // ==========================================
    // 规格验收：SyncSession 初始时间使用注入时钟
    // ==========================================

    [Fact]
    public void SyncSession_InitialTime_UsesInjectedTimeProvider()
    {
        var fixedTime = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(fixedTime);
        var config = new ClipConfig(
            ServerUrl: "https://test",
            AuthToken: "tok",
            TokenExpiresAtUtc: fixedTime.UtcDateTime.AddHours(1),
            Username: "u",
            ClientId: "c",
            ClientName: "cn",
            LastServerVersion: 0,
            MaxTextBytes: 512000,
            HelloTimeoutSeconds: 5,
            HeartbeatIntervalSeconds: 30,
            HeartbeatTimeoutSeconds: 60,
            HashRounds: 100,
            Salt: "salt",
            DerivedKeyBase64: "",
            CipherEnabled: false,
            TrustAllCertificates: true,
            ServerCertificateThumbprint: "",
            RelaunchOnBoot: false,
            WebsocketStatusNotification: false,
            LocalMaxClipboardBytes: 512000);

        var clipboard = new ClipboardBridge(new TestSynchronizationContext());
        var session = new SyncSession(
            config,
            clipboard,
            onStatus: _ => { },
            onRemoteTextApplied: _ => { },
            onServerVersionAdvanced: null,
            timeProvider: timeProvider);

        var field = typeof(SyncSession).GetField("_lastLocalChangeUtc", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var initialVal = (DateTimeOffset)field.GetValue(session)!;
        Assert.Equal(fixedTime, initialVal);

        // Advance time and notify
        var advancedTime = fixedTime.AddMinutes(15);
        timeProvider.SetUtcNow(advancedTime);
        session.NotifyLocalChange();

        var updatedVal = (DateTimeOffset)field.GetValue(session)!;
        Assert.Equal(advancedTime, updatedVal);
    }

    [Fact]
    public void Decision2_CiYaml_ContainsPublishSingleFileGateStep()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var ciYmlPath = Path.Combine(workspaceRoot, ".github", "workflows", "ci.yml");
        if (!File.Exists(ciYmlPath))
        {
            ciYmlPath = Path.Combine(Directory.GetCurrentDirectory(), ".github", "workflows", "ci.yml");
        }

        Assert.True(File.Exists(ciYmlPath), $"ci.yml not found at {ciYmlPath}");
        var text = File.ReadAllText(ciYmlPath);

        Assert.Contains("dotnet publish TextCascadeSharp.csproj -c Release -r win-x64 --no-self-contained", text);

        var buildIdx = text.IndexOf("dotnet build", StringComparison.Ordinal);
        var testIdx = text.IndexOf("dotnet test", StringComparison.Ordinal);
        var publishIdx = text.IndexOf("dotnet publish", StringComparison.Ordinal);

        Assert.True(buildIdx >= 0 && testIdx > buildIdx && publishIdx > testIdx,
            "CI steps must execute in order: build -> test -> publish");
    }
    // ==========================================
    // 决策：自签证书无指纹时的分级风险提示与审计日志
    // ==========================================

    [Fact]
    public void SelfSignedCert_UiText_ContainsWarningStrings()
    {
        Assert.False(string.IsNullOrWhiteSpace(UiText.TrustCertConfirmDialogBody));
        Assert.False(string.IsNullOrWhiteSpace(UiText.RunningUnpinnedCertWarning));
        Assert.False(string.IsNullOrWhiteSpace(UiText.OperationCancelled));
        Assert.Contains("⚠️", UiText.RunningUnpinnedCertWarning);
    }

    [Fact]
    public async Task SelfSignedCert_ClipApiClient_LogsSecurityWarningWhenUnpinned()
    {
        var logFile = Path.Combine(_tempDir, "security_client_test.log");
        Logger.LogPath = logFile;

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(FakeHttpMessageHandler.Json(ContractSamples.LoginSuccess));
        var client = new ClipApiClient();

        await client.LoginAsync(
            "https://your-server:8443/", "alice", "pw", trustAllCertificates: true,
            CancellationToken.None, handler, serverCertificateThumbprint: "");

        var logText = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
        Assert.Contains("[SECURITY]", logText);
        Assert.Contains("TrustAllCertificates is enabled without a certificate thumbprint", logText);
    }
}
