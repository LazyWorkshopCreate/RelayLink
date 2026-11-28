namespace RelayLink.UnitTests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private readonly List<(int Target, TaskCompletionSource Waiter)> timerWaiters = [];
    private int timerCount;
    private long timestamp;
    private DateTimeOffset utcNow = new(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() { lock (gate) return utcNow; }
    public override long GetTimestamp() { lock (gate) return timestamp; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        List<(int Target, TaskCompletionSource Waiter)>? toNotify = null;
        lock (gate)
        {
            timers.Add(timer);
            timerCount++;
            for (var i = timerWaiters.Count - 1; i >= 0; i--)
            {
                if (timerCount >= timerWaiters[i].Target)
                {
                    toNotify ??= [];
                    toNotify.Add(timerWaiters[i]);
                    timerWaiters.RemoveAt(i);
                }
            }
        }
        if (toNotify is not null)
        {
            foreach (var entry in toNotify) entry.Waiter.TrySetResult();
        }
        return timer;
    }

    public Task WaitForTimerCountAsync(int expectedCount, CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        lock (gate)
        {
            if (timerCount >= expectedCount) return Task.CompletedTask;
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            timerWaiters.Add((expectedCount, waiter));
        }
        return waiter.Task.WaitAsync(cancellationToken);
    }

    public void Advance(TimeSpan delta)
    {
        List<(TimerCallback Callback, object? State)> due = [];
        lock (gate)
        {
            timestamp += delta.Ticks;
            utcNow += delta;
            foreach (var timer in timers)
            {
                if (!timer.Disposed && timer.DueAtTicks <= timestamp)
                {
                    due.Add((timer.Callback, timer.State));
                    timer.DueAtTicks = timer.PeriodTicks > 0 ? timer.DueAtTicks + timer.PeriodTicks : long.MaxValue;
                }
            }
        }
        foreach (var item in due) item.Callback(item.State);
    }

    private void Remove(ManualTimer timer)
    {
        lock (gate) timers.Remove(timer);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long DueAtTicks { get; set; } = long.MaxValue;
        public long PeriodTicks { get; set; }
        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (Disposed) return false;
                PeriodTicks = period == Timeout.InfiniteTimeSpan ? 0 : Math.Max(0, period.Ticks);
                DueAtTicks = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : owner.timestamp + Math.Max(0, dueTime.Ticks);
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner.gate) Disposed = true;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
