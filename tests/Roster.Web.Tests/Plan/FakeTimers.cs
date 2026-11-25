// New: vitest's fake timers for C#. A clock that only moves when the test moves it, and a
// one-thread scheduler, so code that awaits timers runs step by step: Advance fires every
// timer that falls due, in time order, and runs everything that follows from each before
// the next (vitest's advanceTimersByTimeAsync).

namespace Roster.Web.Tests.Plan;

internal sealed class FakeTimers : IDisposable
{
    private readonly SynchronizationContext? _previous;
    private readonly Queue<(SendOrPostCallback Work, object? State)> _queue = new();
    private readonly Scheduler _context;

    public FakeTimers(DateTimeOffset start)
    {
        Clock = new ManualClock(start);
        _context = new Scheduler(this);
        _previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_context);
    }

    public ManualClock Clock { get; }

    /// <summary>Move the clock on by <paramref name="ms"/>, firing the timers that fall due on the way.</summary>
    public void Advance(double ms)
    {
        var target = Clock.Now + TimeSpan.FromMilliseconds(ms);
        Drain();
        while (Clock.NextDue(target) is { } timer)
        {
            Clock.Now = timer.Due;
            Clock.Fire(timer);
            Drain();
        }
        Clock.Now = target;
        Drain();
    }

    /// <summary>Run everything waiting to run.</summary>
    public void Drain()
    {
        while (_queue.Count > 0)
        {
            var (work, state) = _queue.Dequeue();
            work(state);
        }
    }

    /// <summary>A task that completes with <paramref name="value"/> after <paramref name="ms"/>.</summary>
    public async Task<T> After<T>(double ms, T value)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(ms), Clock);
        return value;
    }

    /// <summary>A task that fails after <paramref name="ms"/>, as a dropped connection does.</summary>
    public async Task<T> DropAfter<T>(double ms)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(ms), Clock);
        throw new HttpRequestException("Failed to fetch");
    }

    /// <summary>A task that never settles.</summary>
    public static Task<T> Never<T>() => new TaskCompletionSource<T>().Task;

    /// <summary>Watch a task settle while the clock is moved on.</summary>
    public static Tracked<T> Track<T>(Task<T> task)
    {
        var seen = new Tracked<T>();
        task.ContinueWith(
            t =>
            {
                seen.Done = true;
                if (t.IsCompletedSuccessfully) seen.Value = t.Result;
                else seen.Error = t.Exception?.InnerException;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return seen;
    }

    public void Dispose()
    {
        Drain();
        SynchronizationContext.SetSynchronizationContext(_previous);
    }

    private sealed class Scheduler(FakeTimers owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => owner._queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public override SynchronizationContext CreateCopy() => this;
    }
}

internal sealed class Tracked<T>
{
    public bool Done { get; set; }
    public T? Value { get; set; }
    public Exception? Error { get; set; }
}

/// <summary>A TimeProvider whose time and timers only move when <see cref="FakeTimers.Advance"/> moves them.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly List<Timer> _timers = [];
    private long _seq;

    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Now.UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    internal Timer? NextDue(DateTimeOffset until) =>
        _timers.Where(t => t.Due <= until).OrderBy(t => t.Due).ThenBy(t => t.Seq).FirstOrDefault();

    internal void Fire(Timer timer)
    {
        if (timer.Period is { } p && p > TimeSpan.Zero) timer.Due += p;
        else _timers.Remove(timer);
        timer.Callback(timer.State);
    }

    internal sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset Due { get; set; }
        public TimeSpan? Period { get; private set; }
        public long Seq { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock._timers.Remove(this);
            if (dueTime == Timeout.InfiniteTimeSpan) return true;
            Due = clock.Now + dueTime;
            Period = period == Timeout.InfiniteTimeSpan ? null : period;
            Seq = clock._seq++;
            clock._timers.Add(this);
            return true;
        }

        public void Dispose() => clock._timers.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
