namespace TextCascadeSharp.Core;

// 固定档位退避：attempt 从 1 开始，超出序列取最后一档。
public sealed class BackoffStrategy
{
    private readonly TimeSpan[] _delays;

    public BackoffStrategy(IReadOnlyList<TimeSpan> delays)
    {
        if (delays is null || delays.Count == 0)
        {
            throw new ArgumentException("Delays must be non-empty.", nameof(delays));
        }
        _delays = delays.ToArray();
    }

    // 只读镜像：不暴露内部可变数组
    public IReadOnlyList<TimeSpan> Delays => _delays;

    // attempt 从 1 开始；≤0 按 1 处理；超界返回末档
    public TimeSpan GetDelay(int attempt)
    {
        var a = attempt <= 1 ? 1 : attempt;
        var index = a - 1;
        return index < _delays.Length ? _delays[index] : _delays[^1];
    }

    // 普通断开重连退避：1,2,5,10,30,60 秒，之后固定 60s
    public static BackoffStrategy NormalReconnect { get; } = new(
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ]);

    // 服务端维护（bye/close 1001）温和退避：1,2,5,10 秒，之后固定 10s
    public static BackoffStrategy GentleReconnect { get; } = new(
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ]);

    // 会话恢复临时故障退避：2,5,10,20,30 秒
    public static BackoffStrategy SessionTransient { get; } = new(
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30)
    ]);

    // 会话恢复限流退避：固定 30 秒
    public static BackoffStrategy SessionRateLimit { get; } = new(
    [
        TimeSpan.FromSeconds(30)
    ]);
}