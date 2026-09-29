using System.Diagnostics;

namespace TextCascadeSharp.Core;

// UU 远程规避监控器：定期轻量检测当前前台窗口所属进程及后台进程存活。
// 当启用且前台进程匹配目标名单时，触发规避挂起；
// 当切出前台或目标进程退出时，触发恢复；
// 无论前台或后台，只要目标进程存活，即维护 IsProcessRunning 供延时避让使用。
public sealed class UuRemoteEvasionMonitor : IDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(1500);

    private readonly Func<bool> _isEnabled;
    private readonly Func<IReadOnlyList<string>> _getTargetProcesses;
    private readonly Action<bool> _onEvasionStateChanged;
    private readonly Func<string?> _foregroundProcessNameProvider;
    private readonly Func<IReadOnlyList<string>, bool> _processRunningChecker;
    private readonly Action<bool>? _onProcessRunningChanged;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly object _lock = new();

    private ITimer? _timer;
    private bool _running;
    private bool _disposed;

    public UuRemoteEvasionMonitor(
        Func<bool> isEnabled,
        Func<IReadOnlyList<string>> getTargetProcesses,
        Action<bool> onEvasionStateChanged,
        Func<string?>? foregroundProcessNameProvider = null,
        Func<IReadOnlyList<string>, bool>? processRunningChecker = null,
        Action<bool>? onProcessRunningChanged = null,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null)
    {
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _getTargetProcesses = getTargetProcesses ?? throw new ArgumentNullException(nameof(getTargetProcesses));
        _onEvasionStateChanged = onEvasionStateChanged ?? throw new ArgumentNullException(nameof(onEvasionStateChanged));
        _foregroundProcessNameProvider = foregroundProcessNameProvider ?? GetDefaultForegroundProcessName;
        _processRunningChecker = processRunningChecker ?? CheckDefaultProcessesRunning;
        _onProcessRunningChanged = onProcessRunningChanged;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    // 当前是否正处于规避状态（前台激活挂起）
    public bool IsInEvasion { get; private set; }

    // 目标进程是否正在运行（无论前台或后台）
    public bool IsProcessRunning { get; private set; }

    // 监控器是否在运行
    public bool IsRunning => _running;

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _running)
            {
                return;
            }
            _running = true;
            _timer = _timeProvider.CreateTimer(_ => CheckNow(), null, _pollInterval, _pollInterval);
        }
        CheckNow();
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_running)
            {
                return;
            }
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }

        // 停止时若原先处于规避或运行状态，强制恢复
        if (IsInEvasion)
        {
            IsInEvasion = false;
            _onEvasionStateChanged(false);
        }
        if (IsProcessRunning)
        {
            IsProcessRunning = false;
            _onProcessRunningChanged?.Invoke(false);
        }
    }

    // 立即执行一次检测
    public void CheckNow()
    {
        lock (_lock)
        {
            if (_disposed || !_running)
            {
                return;
            }
        }

        if (!_isEnabled())
        {
            if (IsInEvasion)
            {
                IsInEvasion = false;
                _onEvasionStateChanged(false);
            }
            if (IsProcessRunning)
            {
                IsProcessRunning = false;
                _onProcessRunningChanged?.Invoke(false);
            }
            return;
        }

        var currentForeground = _foregroundProcessNameProvider();
        var targets = _getTargetProcesses();
        var foregroundMatches = false;

        if (!string.IsNullOrWhiteSpace(currentForeground) && targets is { Count: > 0 })
        {
            for (var i = 0; i < targets.Count; i++)
            {
                if (string.Equals(currentForeground, targets[i], StringComparison.OrdinalIgnoreCase))
                {
                    foregroundMatches = true;
                    break;
                }
            }
        }

        // 若前台已命中，目标进程必然处于运行状态，无需额外扫描系统进程列表
        var runningMatches = foregroundMatches;
        if (!runningMatches && targets is { Count: > 0 })
        {
            runningMatches = _processRunningChecker(targets);
        }

        if (runningMatches != IsProcessRunning)
        {
            IsProcessRunning = runningMatches;
            _onProcessRunningChanged?.Invoke(IsProcessRunning);
        }

        if (foregroundMatches != IsInEvasion)
        {
            IsInEvasion = foregroundMatches;
            _onEvasionStateChanged(IsInEvasion);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }

        if (IsInEvasion)
        {
            IsInEvasion = false;
            _onEvasionStateChanged(false);
        }
        if (IsProcessRunning)
        {
            IsProcessRunning = false;
            _onProcessRunningChanged?.Invoke(false);
        }
    }

    private static bool CheckDefaultProcessesRunning(IReadOnlyList<string> targetProcesses)
    {
        try
        {
            if (targetProcesses is null || targetProcesses.Count == 0)
            {
                return false;
            }

            var runningProcesses = Process.GetProcesses();
            try
            {
                var targetSet = new HashSet<string>(targetProcesses, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < runningProcesses.Length; i++)
                {
                    if (targetSet.Contains(runningProcesses[i].ProcessName))
                    {
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                for (var i = 0; i < runningProcesses.Length; i++)
                {
                    runningProcesses[i].Dispose();
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static string? GetDefaultForegroundProcessName()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            // 进程已退出、权限隔离或无窗口时安全返回 null
            return null;
        }
    }
}
