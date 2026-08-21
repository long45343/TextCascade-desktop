namespace TextCascadeSharp.Core;

// 重连调度：退避档位、单飞、电源/网络唤醒打断。
// 不拥有回调依赖，只暴露状态转换；由 TextSyncEngine 驱动。
public sealed class ReconnectPolicy
{
    private readonly TimeProvider _timeProvider;
    private readonly BackoffStrategy _normalBackoff;
    private readonly BackoffStrategy _gentleBackoff;
    private CancellationTokenSource _wakeCts = new();
    // 0=无重连在途，1=有（Interlocked 单飞）
    private int _inFlight;
    // 自上次 Reset 后的尝试次数
    private int _attempts;
    // 最近一次 TryBeginReconnect 选定的退避时长（供 WaitForDelayAsync 使用）
    private TimeSpan _currentDelay;

    public ReconnectPolicy(
        TimeProvider? timeProvider = null,
        BackoffStrategy? normalBackoff = null,
        BackoffStrategy? gentleBackoff = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _normalBackoff = normalBackoff ?? BackoffStrategy.NormalReconnect;
        _gentleBackoff = gentleBackoff ?? BackoffStrategy.GentleReconnect;
    }

    // 测试接缝：覆盖退避时长；null=使用退避策略查表
    public TimeSpan? DelayOverride { get; set; }

    public bool IsReconnectInFlight => Volatile.Read(ref _inFlight) != 0;

    public int Attempts => Volatile.Read(ref _attempts);

    // welcome 到达：重置尝试计数，退避从第一档重新开始
    public void Reset() => Volatile.Write(ref _attempts, 0);

    // 单飞 CAS 抢占；成功则尝试次数+1，按 gentle/normal 策略查表并选择退避时长。
    // 返回 false 表示已有重连在途，调用方应放弃本次调度。
    public bool TryBeginReconnect(out TimeSpan delay, bool gentle)
    {
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            delay = default;
            return false;
        }

        var attempt = Interlocked.Increment(ref _attempts);
        delay = DelayOverride ?? (gentle ? _gentleBackoff : _normalBackoff).GetDelay(attempt);
        _currentDelay = delay;
        return true;
    }

    // 重连任务结束：复位单飞标志
    public void EndReconnect() => Volatile.Write(ref _inFlight, 0);

    // 电源/网络恢复：生成新唤醒源并取消旧等待
    public void NotifyWake()
    {
        var old = Interlocked.Exchange(ref _wakeCts, new CancellationTokenSource());
        if (old is not null)
        {
            try
            {
                old.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            old.Dispose();
        }
    }

    // 可被唤醒信号打断的退避等待。唤醒（电源/网络恢复）后延时 1s 让网络
    // 稳定，随后立即重连，不等待当前退避到期；调用方真正取消则直接抛出。
    public async Task WaitForDelayAsync(CancellationToken cancellationToken)
    {
        var wakeCts = _wakeCts;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, wakeCts.Token);
        try
        {
            await Task.Delay(_currentDelay, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 被唤醒信号打断：稍等 1s 后由调用方立即重连
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}