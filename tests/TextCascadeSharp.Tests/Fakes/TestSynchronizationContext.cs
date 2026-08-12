namespace TextCascadeSharp.Tests.Fakes;

// 让 Post 立即执行，方便在无 WinForms 消息循环的单元测试中模拟 UI 上下文。
internal sealed class TestSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
    {
        d(state);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        d(state);
    }
}
