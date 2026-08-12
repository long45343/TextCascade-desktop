using System.Text;

namespace TextCascadeSharp.Core;

// 进程内日志。日志 IO 绝不能拖垮应用：
//   - 写失败静默丢弃，不向调用方抛异常
//   - 单文件超过 1MB 时滚动一代，磁盘上最多保留两份
public static class Logger
{
    private const long MaxLogBytes = 1L * 1024 * 1024;
    private static readonly object Lock = new();

    // 可注入路径（测试用）；默认 %APPDATA%\TextCascade\TextCascade.log
    internal static string LogPath { get; set; } = ComputeDefaultPath();

    private static string OldLogPath => Path.Combine(
        Path.GetDirectoryName(LogPath) ?? ".", "TextCascade.old.log");

    public static void Log(string message)
    {
        Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
    }

    public static void LogError(string message, Exception? error = null)
    {
        var detail = error is null ? string.Empty : Environment.NewLine + error;
        Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " ERROR " + message + detail + Environment.NewLine);
    }

    private static void Write(string line)
    {
        lock (Lock)
        {
            try
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                RotateIfNeeded();
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
            catch
            {
                // 日志永不向外抛异常：写不了就丢，保证启动/运行不被日志打断
            }
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length <= MaxLogBytes)
        {
            return;
        }
        File.Copy(LogPath, OldLogPath, overwrite: true);
        File.Delete(LogPath);
    }

    private static string ComputeDefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "TextCascade", "TextCascade.log");
    }
}
