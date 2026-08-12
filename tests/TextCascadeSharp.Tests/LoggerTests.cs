using TextCascadeSharp.Core;
using Xunit;

namespace TextCascadeSharp.Tests;

public class LoggerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalLogPath;

    public LoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TextCascadeLoggerTests_" + Guid.NewGuid().ToString("N"));
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
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public void Log_WhenDirectoryReadOnly_DoesNotThrow()
    {
        var blockingFile = Path.Combine(_tempDir, "block");
        File.WriteAllText(blockingFile, "not a directory");
        Logger.LogPath = Path.Combine(blockingFile, "nested", "TextCascade.log");

        Logger.Log("should not throw");
    }

    [Fact]
    public void Log_ExceedsCap_Rotates()
    {
        var logPath = Path.Combine(_tempDir, "TextCascade.log");
        Logger.LogPath = logPath;
        File.WriteAllText(logPath, new string('x', 1024 * 1024 + 16));

        Logger.Log("after cap");

        var oldPath = Path.Combine(_tempDir, "TextCascade.old.log");
        Assert.True(File.Exists(logPath));
        Assert.True(File.Exists(oldPath));
        Assert.True(new FileInfo(logPath).Length < 1024 * 1024);
        Assert.True(new FileInfo(oldPath).Length > 1024 * 1024);
    }

    [Fact]
    public void Log_RotationKeepsAtMostTwoGenerations()
    {
        var logPath = Path.Combine(_tempDir, "TextCascade.log");
        Logger.LogPath = logPath;
        File.WriteAllText(logPath, new string('x', 1024 * 1024 + 16));

        Logger.Log("first rotation");
        File.WriteAllText(logPath, new string('y', 1024 * 1024 + 16));
        Logger.Log("second rotation");

        var files = Directory.GetFiles(_tempDir).Select(Path.GetFileName).OrderBy(static name => name).ToArray();
        Assert.Equal(new[] { "TextCascade.log", "TextCascade.old.log" }, files);
    }
}
