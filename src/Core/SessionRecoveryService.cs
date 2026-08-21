namespace TextCascadeSharp.Core;

// 自动会话恢复：保存密码存在 → 静默重登；不存在 → 停止服务并清理会话。
// 从 TrayApplicationContext.RunSessionRecoveryAsync 迁出，App 层只负责接线与 UI 更新。
// 退避经注入的 BackoffStrategy 决定（transient 2/5/10/20/30，rate_limit 固定 30s），
// 等待通过可注入时钟（TimeProvider）实现，便于确定性测试。
public sealed class SessionRecoveryService
{
    private readonly Func<LoginRequest, CancellationToken, Task<LoginResult>> _loginAsync;
    private readonly Func<Task> _stopServiceAsync;
    private readonly Action _clearSession;
    private readonly Action<string> _postStatus;
    private readonly Action _refreshUi;
    private readonly TimeProvider _timeProvider;
    private readonly BackoffStrategy _transientBackoff;
    private readonly BackoffStrategy _rateLimitBackoff;
    private readonly object _gate = new();
    // 当前在途恢复的取消源；新 RunAsync / 手动取消 / 注销 / 退出时终止旧恢复
    private CancellationTokenSource? _currentCts;

    public SessionRecoveryService(
        Func<LoginRequest, CancellationToken, Task<LoginResult>> loginAsync,
        Func<Task> stopServiceAsync,
        Action clearSession,
        Action<string> postStatus,
        Action refreshUi,
        TimeProvider? timeProvider = null,
        BackoffStrategy? transientBackoff = null,
        BackoffStrategy? rateLimitBackoff = null)
    {
        _loginAsync = loginAsync;
        _stopServiceAsync = stopServiceAsync;
        _clearSession = clearSession;
        _postStatus = postStatus;
        _refreshUi = refreshUi;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _transientBackoff = transientBackoff ?? BackoffStrategy.SessionTransient;
        _rateLimitBackoff = rateLimitBackoff ?? BackoffStrategy.SessionRateLimit;
    }

    // 取消当前在途恢复。可重复调用；后续 RunAsync 引发的旧运行会被取消并释放。
    public void Cancel()
    {
        lock (_gate)
        {
            if (_currentCts is not null)
            {
                TryCancel(_currentCts);
            }
        }
    }

    // 启动一次会话恢复。request == null：停止服务 + 清理会话 + 提示，不重试。
    public Task RunAsync(LoginRequest? request)
    {
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _currentCts;
            _currentCts = cts;
        }
        CancelAndDispose(previous);
        return RunCoreAsync(request, cts);
    }

    private async Task RunCoreAsync(LoginRequest? request, CancellationTokenSource cts)
    {
        // 在运行启动瞬间一次性捕获 token：后续运行（新一轮 RunAsync/注销/退出）可能取消并释放此
        // cts，延迟线程若在释放后再读 cts.Token 会抛 ObjectDisposedException。
        var token = cts.Token;
        try
        {
            // 无保存凭据：停止服务并清理会话，提示用户重新登录；不重试
            if (request is null)
            {
                await _stopServiceAsync().ConfigureAwait(false);
                _clearSession();
                _postStatus(CoreStatus.Pack(ErrorCodes.SessionExpiredPleaseLogin));
                _refreshUi();
                return;
            }

            _postStatus(CoreStatus.Pack(ErrorCodes.SessionRecovering));
            await _stopServiceAsync().ConfigureAwait(false);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await _loginAsync(request, token).ConfigureAwait(false);
                    _postStatus(CoreStatus.Pack(ErrorCodes.LoginSuccessful));
                    return;
                }
                catch (OperationCanceledException)
                {
                    // 外部取消（手动登录/注销/重启/退出）：静默返回
                    return;
                }
                catch (InvalidCredentialException error)
                {
                    // 保存的密码已被服务端拒绝：不再重试，等用户手动登录
                    _postStatus(CoreStatus.Pack(ErrorCodes.AutoLoginFailed, error.Message));
                    return;
                }
                catch (RateLimitedException)
                {
                    // 自动重登受 429 约束：按 rateLimitBackoff 退避后重试
                    _postStatus(CoreStatus.Pack(ErrorCodes.LoginRateLimited));
                    if (attempt >= _rateLimitBackoff.Delays.Count)
                    {
                        return;
                    }
                    try
                    {
                        await DelayAsync(_rateLimitBackoff.GetDelay(attempt + 1), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                catch (Exception error)
                {
                    // 服务器重启中的 5xx/网络失败：有界重试
                    _postStatus(CoreStatus.Pack(ErrorCodes.AutoLoginFailed, error.Message));
                    if (attempt >= _transientBackoff.Delays.Count)
                    {
                        return;
                    }
                    try
                    {
                        await DelayAsync(_transientBackoff.GetDelay(attempt + 1), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            CancellationTokenSource? current;
            lock (_gate)
            {
                current = _currentCts;
                if (ReferenceEquals(current, cts))
                {
                    _currentCts = null;
                }
            }
            if (ReferenceEquals(current, cts))
            {
                CancelAndDispose(cts);
            }
        }
    }

    // 可注入时钟的一次性延迟：由 timesource 推进触发完成，或被取消打断。
    // 必须把取消向上传播（抛 OperationCanceledException）而不是吞掉，
    // 否则调用方的退避重试循环在取消后仍会继续触发登录。
    // timer/registration 在 finally 中释放（晚于 await 完成），保证推进前定时器一直存活。
    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration? reg = null;
        ITimer? timer = null;
        try
        {
            reg = cancellationToken.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs);
            timer = _timeProvider.CreateTimer(
                static s => ((TaskCompletionSource)s!).TrySetResult(),
                tcs,
                delay,
                Timeout.InfiniteTimeSpan);
            await tcs.Task.ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 源 CTS 已被更新的一轮恢复释放：按取消处理
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            reg?.Dispose();
            timer?.Dispose();
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }
        TryCancel(cts);
        TryDispose(cts);
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void TryDispose(CancellationTokenSource cts)
    {
        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}