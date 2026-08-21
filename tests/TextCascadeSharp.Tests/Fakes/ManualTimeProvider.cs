namespace TextCascadeSharp.Tests.Fakes;

// 手动推进的 TimeProvider：GetTimestamp 返回手动 ticks，
// CreateTimer 返回可由 Advance 触发的手动 ITimer。
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public void Advance(TimeSpan amount)
    {
        ManualTimer[] due;
        long now;
        lock (_gate)
        {
            _ticks += amount.Ticks;
            now = _ticks;
            due = _timers.Where(timer => timer.IsDue(now)).ToArray();
        }
        foreach (var timer in due)
        {
            timer.Fire(now);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period, _ticks);
            _timers.Add(timer);
            return timer;
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object _state;
        private long _nextDueTicks;
        private TimeSpan _period;
        private bool _disposed;

        public ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period,
            long nowTicks)
        {
            _owner = owner;
            _callback = callback;
            _state = state!;
            _nextDueTicks = nowTicks + dueTime.Ticks;
            _period = period;
        }

        public bool IsDue(long now)
        {
            return !_disposed && _nextDueTicks <= now;
        }

        public void Fire(long now)
        {
            if (_disposed)
            {
                return;
            }
            // 一次性定时器：period 为 Timeout.InfiniteTimeSpan（负值，非重复）时触发一次即完成任务，
            // 否则 _nextDueTicks -= 负周期会陷入无限循环。
            if (_period < TimeSpan.Zero)
            {
                _callback(_state);
                Dispose();
                return;
            }
            while (_nextDueTicks <= now)
            {
                _nextDueTicks += _period.Ticks;
                _callback(_state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }
            lock (_owner._gate)
            {
                _nextDueTicks = _owner.GetTimestamp() + dueTime.Ticks;
                _period = period;
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
