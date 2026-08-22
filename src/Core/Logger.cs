using System.Text;

namespace TextCascadeSharp.Core;

// 进程内日志。日志 IO 绝不能拖垮应用：
//   - 写失败静默丢弃，不向调用方抛异常
//   - 内存维护当前日志字节大小，避免每行触发 Directory.CreateDirectory 和 FileInfo 磁盘探测
//   - 单文件超过 1MB 时滚动一代，磁盘上最多保留两份
public static class Logger
{
    private const long MaxLogBytes = 1L * 1024 * 1024;
    private static readonly object Lock = new();
    private static string _logPath = ComputeDefaultPath();
    private static long _currentLogBytes = -1;

    // 可注入路径（测试用）；默认 %APPDATA%\TextCascade\TextCascade.log
    internal static string LogPath
    {
        get
        {
            lock (Lock)
            {
                return _logPath;
            }
        }
        set
        {
            lock (Lock)
            {
                _logPath = value;
                _currentLogBytes = -1;
            }
        }
    }

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
                if (_currentLogBytes < 0)
                {
                    var directory = Path.GetDirectoryName(LogPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    _currentLogBytes = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
                }

                RotateIfNeeded();
                File.AppendAllText(LogPath, line, Encoding.UTF8);
                _currentLogBytes += Encoding.UTF8.GetByteCount(line);
            }
            catch
            {
                // 日志永不向外抛异常：写不了就丢，并将计数置为 -1 以便下一次重试探测
                _currentLogBytes = -1;
            }
        }
    }

    private static void RotateIfNeeded()
    {
        if (_currentLogBytes < 0)
        {
            try
            {
                _currentLogBytes = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
            }
            catch
            {
                _currentLogBytes = 0;
                return;
            }
        }

        if (_currentLogBytes <= MaxLogBytes)
        {
            return;
        }

        try
        {
            if (File.Exists(LogPath))
            {
                File.Copy(LogPath, OldLogPath, overwrite: true);
                File.Delete(LogPath);
            }
            _currentLogBytes = 0;
        }
        catch
        {
            _currentLogBytes = -1;
        }
    }

    private static string ComputeDefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "TextCascade", "TextCascade.log");
    }
}