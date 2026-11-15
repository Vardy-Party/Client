using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VardyParty.Streaming.Tests.Timeline;

/// <summary>
/// A manual <see cref="TimeProvider"/> that knows when its next timer is due, so the
/// harness jumps straight from one milestone to the next instead of stepping a fixed
/// tick. (<c>FakeTimeProvider</c> can only <c>Advance</c> by a step you choose; it does
/// not expose the next due time, so a 111s harvest would need ~1,100 100ms steps.)
/// Every scripted LocalService delay and health check uses <c>Task.Delay(d, clock)</c>.
/// </summary>
internal sealed class MilestoneClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ClockTimer> _timers = new();
    private DateTimeOffset _now;
    private long _seq;

    public MilestoneClock(DateTimeOffset start)
    {
        Start = start;
        _now = start;
    }

    public DateTimeOffset Start { get; }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    /// <summary>Seconds since the scenario started (t+).</summary>
    public double T => (GetUtcNow() - Start).TotalSeconds;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_gate)
                return _timers.Count == 0 ? null : _timers.Min(t => t.Due);
        }
    }

    /// <summary>Moves to the earliest due timer and fires every timer due at that instant.</summary>
    public bool AdvanceToNext()
    {
        List<ClockTimer> due;
        lock (_gate)
        {
            if (_timers.Count == 0)
                return false;
            var next = _timers.Min(t => t.Due);
            if (next > _now)
                _now = next;
            due = _timers.Where(t => t.Due <= _now).OrderBy(t => t.Due).ThenBy(t => t.Seq).ToList();
            foreach (var t in due)
                _timers.Remove(t);
        }

        foreach (var t in due)
            t.Fire();
        return true;
    }

    private void Schedule(ClockTimer timer, DateTimeOffset due)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
            timer.Due = due;
            timer.Seq = ++_seq;
            _timers.Add(timer);
        }
    }

    private void Unschedule(ClockTimer timer)
    {
        lock (_gate)
            _timers.Remove(timer);
    }

    private sealed class ClockTimer(MilestoneClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public DateTimeOffset Due;
        public long Seq;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
                return false;
            _period = period;
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                clock.Unschedule(this);
                return true;
            }

            clock.Schedule(this, clock.GetUtcNow() + dueTime);
            return true;
        }

        public void Fire()
        {
            if (_disposed)
                return;
            if (_period != Timeout.InfiniteTimeSpan && _period > TimeSpan.Zero)
                clock.Schedule(this, Due + _period);
            callback(state);
        }

        public void Dispose()
        {
            _disposed = true;
            clock.Unschedule(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
