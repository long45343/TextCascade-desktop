using TextCascadeSharp;
using TextCascadeSharp.Core;
using TextCascadeSharp.Tests.Fakes;
using Xunit;

namespace TextCascadeSharp.Tests;

// 判断某个状态信封的领域码是否为 code（Core 层契约：postStatus 收的是 CoreStatus.Pack 信封）
internal static class StatusCode
{
    public static bool Is(string status, string code)
        => CoreStatus.TryUnpack(status, out var c, out _) && c == code;

    // code 匹配且 args[0] 包含 fragment（用于断言退避/ex 详情随信封传递）
    public static bool IsWithArg(string status, string code, string fragment)
        => CoreStatus.TryUnpack(status, out var c, out var args)
           && c == code && args.Length > 0 && args[0].Contains(fragment, StringComparison.Ordinal);
}

// SessionRecoveryService 自动会话恢复状态机测试：
// 用自建 fake loginAsync（记录调用次数 + 可选抛异常序列）+ ManualTimeProvider
// 确定性推进退避（不能靠真实时钟等待），并捕获 postStatus/stop/clear/refresh 到列表供断言。
public class SessionRecoveryServiceTests
{
    private sealed class Harness
    {
        private readonly object _gate = new();
        private readonly List<string> _statuses = new();
        private readonly Exception?[] _throwOn;
        private int _loginCalls;
        private int _stopCount;
        private int _clearCount;
        private int _refreshCount;

        public Harness(params Exception?[] throwOn)
        {
            _throwOn = throwOn;
        }

        public ManualTimeProvider Time { get; } = new();

        public int LoginCalls => Volatile.Read(ref _loginCalls);
        public int StopCount => Volatile.Read(ref _stopCount);
        public int ClearCount => Volatile.Read(ref _clearCount);
        public int RefreshCount => Volatile.Read(ref _refreshCount);

        public string[] Statuses
        {
            get
            {
                lock (_gate)
                {
                    return _statuses.ToArray();
                }
            }
        }

        public bool HasStatus(Func<string, bool> predicate)
        {
            lock (_gate)
            {
                return _statuses.Any(predicate);
            }
        }

        public void PostStatus(string status)
        {
            lock (_gate)
            {
                _statuses.Add(status);
            }
        }

        public async Task<LoginResult> LoginAsync(LoginRequest _, CancellationToken ct)
        {
            int call = Interlocked.Increment(ref _loginCalls);
            if (call - 1 < _throwOn.Length && _throwOn[call - 1] is { } ex)
            {
                throw ex;
            }
            return new LoginResult(
                "https://your-server:8443", "tok", DateTime.UtcNow.AddDays(1), 1,
                512_000, 5, 30, 60);
        }

        public Task StopServiceAsync()
        {
            Interlocked.Increment(ref _stopCount);
            return Task.CompletedTask;
        }

        public void ClearSession() => Interlocked.Increment(ref _clearCount);

        public void RefreshUi() => Interlocked.Increment(ref _refreshCount);

        public SessionRecoveryService Create(
            BackoffStrategy? transient = null,
            BackoffStrategy? rateLimit = null)
            => new(LoginAsync, StopServiceAsync, ClearSession, PostStatus, RefreshUi, Time, transient, rateLimit);
    }

    private static LoginRequest TestRequest() =>
        new("https://your-server:8443", "alice", "pw", ClipConfig.DefaultHashRounds, "salt", false);

    [Fact]
    public async Task RunAsync_NullRequest_StopsAndClears()
    {
        var h = new Harness();
        var service = h.Create();

        await service.RunAsync(null);

        Assert.Equal(1, h.StopCount);
        Assert.Equal(1, h.ClearCount);
        Assert.Equal(1, h.RefreshCount);
        Assert.Equal(0, h.LoginCalls); // loginAsync 未被调用，不重试
        Assert.Contains(h.Statuses, s => StatusCode.Is(s, ErrorCodes.SessionExpiredPleaseLogin));
    }

    [Fact]
    public async Task RunAsync_InvalidCredentials_StopsRetrying()
    {
        var h = new Harness(new InvalidCredentialException("bad cred"));
        var service = h.Create();

        await service.RunAsync(TestRequest());

        Assert.Equal(1, h.LoginCalls); // 明确拒绝凭据：不再重试
        Assert.Contains(h.Statuses, s => StatusCode.Is(s, ErrorCodes.AutoLoginFailed));
    }

    [Fact]
    public async Task RunAsync_RateLimited_Waits30s_ThenRetries()
    {
        var h = new Harness(new RateLimitedException("rate"));
        var service = h.Create();
        var run = service.RunAsync(TestRequest());

        await TestHelpers.WaitUntil(() => h.Statuses.Any(s => StatusCode.Is(s, ErrorCodes.LoginRateLimited)));
        await Task.Delay(20); // 确保限流退避 timer 已创建，再推进时钟
        h.Time.Advance(TimeSpan.FromSeconds(30)); // 限流退避固定 30s

        await run;
        Assert.Equal(2, h.LoginCalls);
        Assert.Contains(h.Statuses, s => StatusCode.Is(s, ErrorCodes.LoginRateLimited));
        Assert.Contains(h.Statuses, s => StatusCode.Is(s, ErrorCodes.LoginSuccessful));
    }

    [Fact]
    public async Task RunAsync_RateLimited_Exhausted_Stops()
    {
        // 限流退避仅 1 档（30s）：最多 2 次登录尝试后尝试数 >= 档位数 → 终止
        var h = new Harness(
            new RateLimitedException("r1"),
            new RateLimitedException("r2"),
            new RateLimitedException("r3"));
        var service = h.Create();
        var run = service.RunAsync(TestRequest());

        await TestHelpers.WaitUntil(() => h.Statuses.Any(s => StatusCode.Is(s, ErrorCodes.LoginRateLimited)));
        await Task.Delay(20); // 确保限流退避 timer 已创建，再推进时钟
        h.Time.Advance(TimeSpan.FromSeconds(30));

        await run;
        Assert.Equal(2, h.LoginCalls); // 未超过 rateLimitBackoff 档位数
        Assert.DoesNotContain(h.Statuses, s => StatusCode.Is(s, ErrorCodes.LoginSuccessful));
    }

    [Fact]
    public async Task RunAsync_Transient_BackoffSequence()
    {
        // 5 档瞬态退避 2/5/10/20/30s：逐档推进并验证顺序命中，最终成功
        var h = new Harness(
            new Exception("transient-1"),
            new Exception("transient-2"),
            new Exception("transient-3"),
            new Exception("transient-4"),
            new Exception("transient-5"));
        var service = h.Create();
        var run = service.RunAsync(TestRequest());
        var delays = BackoffStrategy.SessionTransient.Delays;

        for (int stage = 0; stage < delays.Count; stage++)
        {
            int stageNo = stage + 1;
            await TestHelpers.WaitUntil(() => h.HasStatus(s => StatusCode.IsWithArg(s, ErrorCodes.AutoLoginFailed, $"transient-{stageNo}")));
            await Task.Delay(20); // 确保该档等待的 timer 已创建

            // 推进不到整档时不应触发重试
            h.Time.Advance(delays[stage] - TimeSpan.FromSeconds(0.5));
            Assert.Equal(stageNo, h.LoginCalls);

            // 跨过整档阈值后触发下一次登录尝试
            h.Time.Advance(TimeSpan.FromSeconds(1));
            await TestHelpers.WaitUntil(() => h.LoginCalls >= stageNo + 1);
        }

        await run;
        Assert.Equal(delays.Count + 1, h.LoginCalls); // 5 次报错 + 最终成功
        Assert.Contains(h.Statuses, s => StatusCode.Is(s, ErrorCodes.LoginSuccessful));
    }

    [Fact]
    public async Task RunAsync_Transient_Exhausted_Stops()
    {
        // 每次都是瞬态异常：推进第 5 档（30s）后第 6 次尝试 attempt=5 >= 档位数 → 终止
        var h = new Harness(
            new Exception("t1"),
            new Exception("t2"),
            new Exception("t3"),
            new Exception("t4"),
            new Exception("t5"),
            new Exception("t6"));
        var service = h.Create();
        var run = service.RunAsync(TestRequest());
        var delays = BackoffStrategy.SessionTransient.Delays;

        for (int i = 0; i < delays.Count; i++)
        {
            await TestHelpers.WaitUntil(() => h.HasStatus(s => StatusCode.IsWithArg(s, ErrorCodes.AutoLoginFailed, $"t{i + 1}")));
            await Task.Delay(20);
            h.Time.Advance(delays[i]);
            await TestHelpers.WaitUntil(() => h.LoginCalls >= i + 2);
        }

        await TestHelpers.WaitUntil(() => run.IsCompleted);
        await Task.Delay(50);
        Assert.Equal(delays.Count + 1, h.LoginCalls); // 次数耗尽即终止，不再多调
        Assert.DoesNotContain(h.Statuses, s => StatusCode.Is(s, ErrorCodes.LoginSuccessful));
    }

    [Fact]
    public async Task RunAsync_Success_AfterRetry_Returns()
    {
        // 首两次稳定故障，之后成功：应恰好重试后收获成功
        var h = new Harness(new Exception("flaky-1"), new Exception("flaky-2"));
        var service = h.Create();
        var run = service.RunAsync(TestRequest());
        var delays = BackoffStrategy.SessionTransient.Delays;

        for (int i = 0; i < 2; i++)
        {
            await TestHelpers.WaitUntil(() => h.HasStatus(s => StatusCode.IsWithArg(s, ErrorCodes.AutoLoginFailed, $"flaky-{i + 1}")));
            await Task.Delay(20); // 确保该档等待的 timer 已创建，再推进时钟
            h.Time.Advance(delays[i]);
            await TestHelpers.WaitUntil(() => h.LoginCalls >= i + 2);
        }

        await run;
        Assert.Equal(3, h.LoginCalls);
        Assert.Contains(h.Statuses, s => StatusCode.Is(s, ErrorCodes.LoginSuccessful));
    }

    [Fact]
    public async Task Cancel_DuringWait_ReturnsImmediately()
    {
        var h = new Harness(new Exception("transient-1"));
        var service = h.Create();
        var run = service.RunAsync(TestRequest());

        await TestHelpers.WaitUntil(() => h.HasStatus(s => StatusCode.IsWithArg(s, ErrorCodes.AutoLoginFailed, "transient-1")));
        service.Cancel();

        await run; // 取消期间退出：不抛
        await Task.Delay(50);
        Assert.Equal(1, h.LoginCalls); // 取消后不再有 loginAsync 调用
    }

    [Fact]
    public async Task Cancel_PreviousRun_DisposesOld()
    {
        var h = new Harness(new Exception("transient-1"));
        var service = h.Create();
        var run1 = service.RunAsync(TestRequest());
        await TestHelpers.WaitUntil(() => h.HasStatus(s => StatusCode.IsWithArg(s, ErrorCodes.AutoLoginFailed, "transient-1")));

        var run2 = service.RunAsync(TestRequest()); // 新一轮接管：取消旧 CTS

        await run2;
        await run1; // 旧一轮被取消，静默返回
        await TestHelpers.WaitUntil(() => h.LoginCalls >= 2);
        Assert.Equal(2, h.LoginCalls); // 旧 run 一次 + 新 run 一次
        Assert.Contains(h.Statuses, s => StatusCode.Is(s, ErrorCodes.LoginSuccessful));
    }
}
