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

    [Fact]
    public void Bug1_ReadFailure_DoesNotConsumeSequence_PollRetries()
    {
        var notified = new List<string>();
        uint sequence = 100;
        var failRead = true;

        using var monitor = new ClipboardMonitor(
            onClipboardChanged: t => notified.Add(t),
            getSequenceNumberOverride: () => sequence,
            getTextOverride: () =>
            {
                if (failRead)
                {
                    throw new ExternalException("Simulated clipboard lock");
                }
                return "hello";
            })
        {
            RetryAttempts = 0 // 禁用直接 short retry，验证轮询兜底
        };

        monitor.Start();
        Assert.Empty(notified);

        // Sequence changed from 100 to 101 -> ReadAndNotify fails and does not consume 101
        sequence = 101;
        monitor.OnPollTick();
        Assert.Empty(notified);

        // Next tick: sequence is still 101, but previous failure left _lastSequenceNumber as initial -> retries and succeeds
        failRead = false;
        monitor.OnPollTick();

        Assert.Single(notified);
        Assert.Equal("hello", notified[0]);
    }

    [Fact]
    public void Bug1_NonTextAndDedup_ConsumeSequence()
    {
        var readCount = 0;
        uint sequence = 100;
        string? currentText = null;

        using var monitor = new ClipboardMonitor(
            onClipboardChanged: _ => { },
            getSequenceNumberOverride: () => sequence,
            getTextOverride: () =>
            {
                readCount++;
                return currentText;
            })
        {
            RetryAttempts = 0
        };

        monitor.Start();
        Assert.Equal(1, readCount);

        // Non-text (null) consumed sequence -> subsequent poll with same sequence does not re-read
        monitor.OnPollTick();
        Assert.Equal(1, readCount);

        // Same content dedup consumed sequence
        sequence = 101;
        currentText = "same";
        monitor.OnPollTick(); // reads and consumes seq 101
        Assert.Equal(2, readCount);

        monitor.OnPollTick(); // same seq 101 -> no read
        Assert.Equal(2, readCount);
    }

    [Fact]
    public async Task Bug1_Retry_DedupedSingleFlight()
    {
        var readAttempts = 0;
        uint sequence = 100;

        using var monitor = new ClipboardMonitor(
            onClipboardChanged: _ => { },
            getSequenceNumberOverride: () => sequence,
            getTextOverride: () =>
            {
                Interlocked.Increment(ref readAttempts);
                throw new ExternalException("Locked");
            })
        {
            RetryAttempts = 3,
            RetryDelay = TimeSpan.Zero
        };

        monitor.Start();
        // Allow background async retry chain to complete
        await TestHelpers.WaitUntil(() => Volatile.Read(ref readAttempts) >= 4);

        // Initial 1 + 3 retries = 4 attempts total
        Assert.Equal(4, readAttempts);
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

    [Fact]
    public void Decision6_EntireSourceDirectory_ContainsZeroReferencesToWinForms()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var srcDir = Path.Combine(workspaceRoot, "src");
        if (!Directory.Exists(srcDir))
        {
            srcDir = Path.Combine(Directory.GetCurrentDirectory(), "src");
        }

        Assert.True(Directory.Exists(srcDir), $"src not found at {srcDir}");
        var csFiles = Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories);

        foreach (var file in csFiles)
        {
            var fileName = Path.GetFileName(file);
            var content = File.ReadAllText(file);
            Assert.False(content.Contains("using System.Windows.Forms"),
                $"File {fileName} in src contains reference to System.Windows.Forms, which violates Route C pure NativeAOT!");
        }
    }

    // ==========================================
    // 回归守卫：自绘下拉框不得再用 ComboBox 消息查询状态
    // ==========================================
    // 下拉框已由原生 COMBOBOX 改为自绘 BUTTON（配合 FluentPopupMenu 弹层）。
    // CB_GETCURSEL 对非 ComboBox 控件恒返回 0，曾导致「指定证书 SHA-256 指纹」模式下
    // 指纹输入框的外框永远不绘制（白底输入框在白色卡片上消失）。
    [Fact]
    public void FluentDropdowns_DoNotQueryComboBoxMessages()
    {
        var srcDir = ResolveSrcDirectory();
        var content = File.ReadAllText(Path.Combine(srcDir, "App", "NativeConfigWindow.cs"));

        Assert.False(content.Contains("CB_GETCURSEL"),
            "NativeConfigWindow 的下拉框是自绘 BUTTON，CB_GETCURSEL 对其恒返回 0；请直接使用 _currentCertMode/_currentUnitIndex 字段。");
        Assert.False(content.Contains("CBS_DROPDOWNLIST"),
            "窗口内下拉框应保持自绘 Fluent 按钮，不应重新引入原生 COMBOBOX。");
        Assert.False(content.Contains("TrackPopupMenuEx"),
            "窗口内下拉弹层应使用 FluentPopupMenu，不应回退到系统原生方形菜单。");
    }

    // ==========================================
    // 回归守卫：布局必须随 DPI 缩放，不能只缩放字体
    // ==========================================
    // 字体由 CreateFont 按 DPI 换算像素高度，但布局曾全部写死 96dpi 数值，
    // 导致高 DPI 下文字比容器大一倍：标题被截成 "TextCa"、标签字形被切掉半个。
    [Fact]
    public void ConfigWindowLayout_ScalesWithDpi()
    {
        var srcDir = ResolveSrcDirectory();
        var content = File.ReadAllText(Path.Combine(srcDir, "App", "NativeConfigWindow.cs"));

        // 必须存在统一的缩放函数与 DPI 字段
        Assert.Contains("private double _scale", content);
        Assert.Contains("private int S(int logical)", content);
        Assert.Contains("WM_DPICHANGED", content);

        // 窗口尺寸必须经过 S() 缩放，而不是写死裸像素值
        Assert.Contains("SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, S(", content);
    }

    // 弹层与弹窗同样必须跟随父窗口 DPI，否则与缩放后的主窗口比例不一致
    [Fact]
    public void PopupAndDialog_ScaleWithParentDpi()
    {
        var srcDir = ResolveSrcDirectory();

        var popup = File.ReadAllText(Path.Combine(srcDir, "App", "FluentPopupMenu.cs"));
        Assert.Contains("GetDpiForWindow(hOwner)", popup);
        Assert.Contains("Metrics.FromDpi", popup);

        var dialog = File.ReadAllText(Path.Combine(srcDir, "App", "FluentDialog.cs"));
        Assert.Contains("GetDpiForWindow(hParent)", dialog);
        Assert.Contains("state.Scale", dialog);
    }

    // ==========================================
    // 回归守卫：运行期 DPI 变化必须整体重建子控件
    // ==========================================
    // 仅重排会漏掉 _rcCard1、_editBoxes 里的圆角外框坐标和各按钮字体（都在创建时写入一次），
    // 表现为拖动窗口跨屏后 UI 撕裂。重建又必须保住用户未保存的输入与状态栏文本。
    [Fact]
    public void RuntimeDpiChange_RebuildsChildrenAndPreservesInput()
    {
        var srcDir = ResolveSrcDirectory();
        var content = File.ReadAllText(Path.Combine(srcDir, "App", "NativeConfigWindow.cs"));

        Assert.Contains("DestroyChildControls()", content);
        Assert.Contains("WM_SETREDRAW", content);
        // 重建前保存输入、重建后写回
        Assert.Contains("var typedServerUrl = GetControlText", content);
        Assert.Contains("RefreshFromState();", content);
    }

    // ==========================================
    // 回归守卫：弹窗必须无系统标题栏且文本居中
    // ==========================================
    [Fact]
    public void FluentDialog_HasNoCaptionBarAndCenteredText()
    {
        var srcDir = ResolveSrcDirectory();
        var dialog = File.ReadAllText(Path.Combine(srcDir, "App", "FluentDialog.cs"));

        Assert.DoesNotContain("WS_CAPTION", dialog);
        Assert.Contains("WS_EX_TOOLWINDOW", dialog);
        Assert.Contains("NativeMethods.DT_CENTER", dialog);
        // 无标题栏则不应再设置窗口图标
        Assert.DoesNotContain("WM_SETICON", dialog);
    }

    // ==========================================
    // 回归守卫：卡片图标按绘制尺寸取图
    // ==========================================
    // 固定加载 24px 再被 GDI 放大是高 DPI 下图标毛刺的根因，必须按目标像素尺寸选 ICO 条目。
    [Fact]
    public void CardIcons_LoadAtRequestedSize()
    {
        var srcDir = ResolveSrcDirectory();
        var icons = File.ReadAllText(Path.Combine(srcDir, "App", "AppIcons.cs"));
        Assert.Contains("ConnectionIcon(int size)", icons);
        Assert.Contains("CardIcon(\"icon_conn.ico\", size)", icons);

        var window = File.ReadAllText(Path.Combine(srcDir, "App", "NativeConfigWindow.cs"));
        Assert.Contains("AppIcons.ConnectionIcon(S(20))", window);
    }

    // ==========================================
    // 回归守卫：对勾使用 MiSans 字形，正文用 Semibold 真字重
    // ==========================================
    // GDI 折线画的勾在复选框与下拉项里都显得生硬；改用字体里的 U+2713 才有笔画变化与抗锯齿。
    // 同时正文统一 Semibold 轮廓，避免 GDI 在 Normal 上合成加粗造成涂抹。
    [Fact]
    public void CheckGlyphs_UseMiSansGlyphAndSemiboldBody()
    {
        var srcDir = ResolveSrcDirectory();

        var window = File.ReadAllText(Path.Combine(srcDir, "App", "NativeConfigWindow.cs"));
        Assert.Contains("DrawCheckGlyph", window);
        Assert.Contains("\u2713", window);

        var popup = File.ReadAllText(Path.Combine(srcDir, "App", "FluentPopupMenu.cs"));
        Assert.Contains("\u2713", popup);

        var fonts = File.ReadAllText(Path.Combine(srcDir, "App", "EmbeddedFonts.cs"));
        Assert.Contains("misans-semibold.ttf", fonts);

        // 字体族本身已是半粗，不应再请求 FW_SEMIBOLD 让 GDI 二次合成加粗
        Assert.DoesNotContain("FW_SEMIBOLD", window);
        Assert.DoesNotContain("FW_SEMIBOLD", File.ReadAllText(Path.Combine(srcDir, "App", "FluentDialog.cs")));
    }

    // ==========================================
    // 回归守卫：弹层悬停时文字背景必须跟随高亮色
    // ==========================================
    // 文字用 OPAQUE 绘制，若背景色恒为面板底色，悬停行上会盖出白底方块。
    [Fact]
    public void PopupHover_TextBackgroundMatchesHighlight()
    {
        var srcDir = ResolveSrcDirectory();
        var popup = File.ReadAllText(Path.Combine(srcDir, "App", "FluentPopupMenu.cs"));
        Assert.Contains("var rowBg =", popup);
        Assert.Contains("NativeMethods.SetBkColor(hdc, rowBg)", popup);
    }

    // ==========================================
    // 回归守卫：Core → UI 状态回流通道不得再被清空
    // ==========================================
    // 删除底部状态栏时，SetStatus 曾被整个掏成空方法体：Core 投递的所有状态
    // （登录失败、连接断开、剪贴板写入失败）全部静默丢失，用户点按钮后毫无反馈。
    [Fact]
    public void StatusFeedback_RoutesThroughFormatAndToast()
    {
        var srcDir = ResolveSrcDirectory();
        var window = File.ReadAllText(Path.Combine(srcDir, "App", "NativeConfigWindow.cs"));

        // 信封必须解包成文案，而不是被丢弃
        Assert.Contains("UiText.FormatStatus(message)", window);
        Assert.Contains("private void PublishStatus(", window);
        Assert.Contains("ShowStatusToast(", window);

        // SetStatus 不得退回空实现
        Assert.DoesNotContain("public void SetStatus(string message)\n    {\n    }", window);

        // 自动登录失败必须走 CoreStatus 信封，否则 IsProblemStatus 无法分类
        var host = File.ReadAllText(Path.Combine(srcDir, "App", "TrayHost.cs"));
        Assert.DoesNotContain("PostStatus(UiText.AutoLoginFailed", host);
        Assert.Contains("CoreStatus.Pack(ErrorCodes.AutoLoginFailed", host);
    }

    // 问题状态与过程态必须分开：前者提示用户，后者只留痕（否则每次收发都弹提示）
    [Fact]
    public void StatusClassification_SeparatesProblemsFromChatter()
    {
        Assert.True(UiText.IsProblemStatus(CoreStatus.Pack(ErrorCodes.Disconnected, "timeout")));
        Assert.True(UiText.IsProblemStatus(CoreStatus.Pack(ErrorCodes.ClipboardWriteFailed, "locked")));
        Assert.True(UiText.IsProblemStatus(CoreStatus.Pack(ErrorCodes.SessionExpiredPleaseLogin)));
        Assert.True(UiText.IsProblemStatus(CoreStatus.Pack(ErrorCodes.AutoLoginFailed, "boom")));

        // 过程态绝不能被判成问题，否则同步期间会持续弹卡片
        Assert.False(UiText.IsProblemStatus(CoreStatus.Pack(ErrorCodes.Connecting)));
        Assert.False(UiText.IsProblemStatus(CoreStatus.Pack(ErrorCodes.Connected)));
        Assert.False(UiText.IsProblemStatus(CoreStatus.Pack(ErrorCodes.Broadcasting)));
        Assert.False(UiText.IsProblemStatus(CoreStatus.Pack(ErrorCodes.SessionRecovering)));
        Assert.False(UiText.IsProblemStatus(CoreStatus.Pack(ErrorCodes.LoginSuccessful)));

        Assert.True(UiText.IsChatterStatus(CoreStatus.Pack(ErrorCodes.Connecting)));
        Assert.False(UiText.IsChatterStatus(CoreStatus.Pack(ErrorCodes.Disconnected)));

        // UI 层直接传入的裸文案不带信封前缀，应视为普通信息
        Assert.False(UiText.IsProblemStatus(UiText.RequiredLoginFields));
        Assert.False(UiText.IsChatterStatus(UiText.RequiredLoginFields));
    }

    // 领域异常必须映射成本地化文案，不能再把英文技术描述直接抛给用户
    [Fact]
    public void StatusFeedback_LocalizesDomainExceptions()
    {
        var invalid = UiText.FormatException(new InvalidCredentialException("x"));
        Assert.DoesNotContain("Invalid credentials rejected by server", invalid);
        Assert.False(string.IsNullOrWhiteSpace(invalid));

        var limited = UiText.FormatException(new RateLimitedException("x"));
        Assert.DoesNotContain("rate limited by server", limited);

        // HTTP 状态码必须原样保留在文案里，便于用户与运维对齐
        var request = UiText.FormatException(new CoreException(ErrorCodes.LoginRequestFailed, "HTTP 503"));
        Assert.Contains("503", request);

        var version = UiText.FormatException(new ProtocolVersionNotSupportedException(9, 1));
        Assert.Contains("9", version);
        Assert.Contains("1", version);
    }

    // CoreException 必须单独保留原始 detail，UI 层才不必从 Message 里反拆拼接串
    [Fact]
    public void CoreException_PreservesRawDetail()
    {
        var error = new CoreException(ErrorCodes.LoginRequestFailed, "HTTP 500");
        Assert.Equal(ErrorCodes.LoginRequestFailed, error.ErrorCode);
        Assert.Equal("HTTP 500", error.Detail);

        var withoutDetail = new CoreException(ErrorCodes.LoginResponseInvalid);
        Assert.Null(withoutDetail.Detail);
    }

    private static string ResolveSrcDirectory()
    {
        var workspaceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var srcDir = Path.Combine(workspaceRoot, "src");
        if (!Directory.Exists(srcDir))
        {
            srcDir = Path.Combine(Directory.GetCurrentDirectory(), "src");
        }
        Assert.True(Directory.Exists(srcDir), $"src not found at {srcDir}");
        return srcDir;
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

        Assert.Contains("dotnet publish TextCascadeSharp.csproj -c Release -r win-x64", text);

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

    // ==========================================
    // 决策：剪贴板异常精准捕获与 ISyncTransportSender 接口解耦
    // ==========================================

    [Fact]
    public void ClipboardMonitor_ReadAndNotify_CatchesExternalExceptionQuietly_AndLogsUnexpectedException()
    {
        var logFile = Path.Combine(_tempDir, "clipboard_monitor_test.log");
        Logger.LogPath = logFile;

        // 1. ExternalException 应该被静默吸收，不打日志
        using var monitor1 = new ClipboardMonitor(
            onClipboardChanged: _ => { },
            getSequenceNumberOverride: () => 1,
            getTextOverride: () => throw new ExternalException("Clipboard busy"));

        monitor1.Start();
        var logText1 = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
        Assert.DoesNotContain("Unexpected error in ClipboardMonitor.ReadAndNotify", logText1);

        // 2. 非预期异常（如 InvalidOperationException）应该被记录到日志
        using var monitor2 = new ClipboardMonitor(
            onClipboardChanged: _ => { },
            getSequenceNumberOverride: () => 2,
            getTextOverride: () => throw new InvalidOperationException("Unexpected bug"));

        monitor2.Start();
        var logText2 = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
        Assert.Contains("Unexpected error in ClipboardMonitor.ReadAndNotify", logText2);
    }

    [Fact]
    public async Task SyncSession_SendLocalTextAsync_UsesInjectedISyncTransportSender()
    {
        var fixedTime = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(fixedTime);
        var config = new ClipConfig(
            ServerUrl: "https://server.test:8443",
            AuthToken: "tok",
            TokenExpiresAtUtc: fixedTime.UtcDateTime.AddHours(1),
            Username: "alice",
            ClientId: "test-client",
            ClientName: "TestClient",
            LastServerVersion: 0,
            MaxTextBytes: 512000,
            HelloTimeoutSeconds: 5,
            HeartbeatIntervalSeconds: 30,
            HeartbeatTimeoutSeconds: 60,
            HashRounds: 100,
            Salt: "salt",
            DerivedKeyBase64: "",
            CipherEnabled: false,
            TrustAllCertificates: false,
            ServerCertificateThumbprint: "",
            RelaunchOnBoot: false,
            WebsocketStatusNotification: false,
            LocalMaxClipboardBytes: 512000);

        var clipboard = new ClipboardBridge(new TestSynchronizationContext());
        var statuses = new List<string>();
        var session = new SyncSession(
            config,
            clipboard,
            onStatus: s => statuses.Add(s),
            onRemoteTextApplied: _ => { },
            onServerVersionAdvanced: null,
            timeProvider: timeProvider);

        session.SetConnected(true);

        var fakeSender = new FakeTransportSender();
        await session.SendLocalTextAsync("hello world", "test", fakeSender, CancellationToken.None);

        Assert.Single(fakeSender.SentClips);
        Assert.Equal("hello world", fakeSender.SentClips[0].Payload);
        Assert.Contains(statuses, s => s.Contains(ErrorCodes.Broadcasting));
    }

    private sealed class FakeTransportSender : ISyncTransportSender
    {
        public List<OutboundClipMessage> SentClips { get; } = new();

        public Task SendClipAsync(OutboundClipMessage clip, CancellationToken cancellationToken)
        {
            SentClips.Add(clip);
            return Task.CompletedTask;
        }
    }
}


