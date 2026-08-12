using TextCascadeSharp.Core;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TextCascadeSharp.Tests.Fakes;

internal sealed class TestStompListener : IStompListener
{
    private readonly object _gate = new();
    private readonly List<string> _messages = new();

    public TaskCompletionSource ConnectedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<Exception> ErrorTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public int OnErrorCount { get; private set; }

    public Task OnConnectedAsync()
    {
        ConnectedTcs.TrySetResult();
        return Task.CompletedTask;
    }

    public Task OnMessageAsync(string body)
    {
        lock (_gate)
        {
            _messages.Add(body);
        }
        return Task.CompletedTask;
    }

    public Task OnClosedAsync(string reason)
    {
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception error)
    {
        OnErrorCount++;
        ErrorTcs.TrySetResult(error);
        return Task.CompletedTask;
    }
}

internal static class TestHelpers
{
    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
