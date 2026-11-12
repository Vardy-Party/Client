namespace VardyParty.Streaming;

/// <summary>
/// Caps LocalService browser work at <see cref="MaxConcurrent"/> plays. A startup race
/// or playback-priority window marks itself with <see cref="BeginRace"/> /
/// <see cref="BeginPlaybackPriority"/> and <see cref="MarkRaceLeg"/> so attach /
/// prewarm <c>/play</c> calls use the slots, and candidate testing waits instead of
/// stacking more Chromium on top.
/// </summary>
public sealed class ResolverRaceThrottle
{
    public const int MaxConcurrent = 2;

    /// <summary>
    /// How long find-phase stays paused after first playback starts so Next can take
    /// a warm URL (unless prewarm depth is satisfied sooner).
    /// </summary>
    public static TimeSpan PlaybackPriorityTimeout =>
        VardyParty.Kernel.FirstPlayOpenStrategy.PlaybackPriorityTimeout;

    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    /// <summary>
    /// Slots held right now by <c>/play</c>, <c>/mp</c> or a race leg. Read-only seam for the
    /// probe timeline harness (docs/PROBE_SCHEDULING_RULES.md); scheduling never reads it.
    /// </summary>
    internal int SlotsInUse => MaxConcurrent - _slots.CurrentCount;
    private readonly object _gate = new();
    private int _races;
    private int _othersHeld;
    private TaskCompletionSource _raceEnded = Completed();
    private TaskCompletionSource _othersDrained = Completed();
    private static readonly AsyncLocal<int> RaceLegDepth = new();

    /// <summary>
    /// True while a startup open/race or the early-Next playback-priority window holds
    /// the find-phase pause.
    /// </summary>
    public bool RaceInFlight
    {
        get
        {
            lock (_gate)
                return _races > 0;
        }
    }

    /// <summary>Marks a playback race or attach as running. Dispose when finished.</summary>
    public IDisposable BeginRace() => BeginHold();

    /// <summary>
    /// Pauses find-phase Chromium for the early-Next window (same hold as
    /// <see cref="BeginRace"/>). Dispose when prewarm depth is warm or the timeout fires.
    /// </summary>
    public IDisposable BeginPlaybackPriority() => BeginHold();

    private IDisposable BeginHold()
    {
        lock (_gate)
        {
            _races++;
            if (_races == 1)
                _raceEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return new Release(EndRace);
    }

    /// <summary>
    /// Marks the current async flow as a race leg so <see cref="EnterAsync"/> takes a
    /// slot during the race instead of waiting for it to end.
    /// </summary>
    public IDisposable MarkRaceLeg()
    {
        RaceLegDepth.Value++;
        return new Release(() => RaceLegDepth.Value--);
    }

    /// <summary>Waits until plays that started before the race have released their slots.</summary>
    public async Task DrainOthersAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task drained;
            lock (_gate)
            {
                if (_othersHeld == 0)
                    return;
                drained = _othersDrained.Task;
            }

            await drained.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Candidate testing awaits this while <see cref="RaceInFlight"/> and nothing else is in flight.</summary>
    public Task WaitUntilRaceEndsAsync(CancellationToken cancellationToken)
    {
        Task ended;
        lock (_gate)
            ended = _raceEnded.Task;
        return ended.WaitAsync(cancellationToken);
    }

    /// <summary>Takes one LocalService play slot. Non-race callers wait out a running race.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        if (RaceLegDepth.Value > 0)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Release(() => _slots.Release());
        }

        while (true)
        {
            Task ended;
            lock (_gate)
                ended = _raceEnded.Task;
            if (!ended.IsCompleted)
            {
                await ended.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_raceEnded.Task.IsCompleted)
                {
                    _slots.Release();
                    continue;
                }

                _othersHeld++;
                if (_othersHeld == 1)
                    _othersDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return new Release(ReleaseOther);
        }
    }

    private void EndRace()
    {
        TaskCompletionSource? ended = null;
        lock (_gate)
        {
            _races = Math.Max(0, _races - 1);
            if (_races == 0)
                ended = _raceEnded;
        }

        ended?.TrySetResult();
    }

    private void ReleaseOther()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            _othersHeld = Math.Max(0, _othersHeld - 1);
            if (_othersHeld == 0)
                drained = _othersDrained;
        }

        _slots.Release();
        drained?.TrySetResult();
    }

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult();
        return tcs;
    }

    private sealed class Release(Action release) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                release();
        }
    }
}
