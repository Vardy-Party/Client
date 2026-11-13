using System.Collections.Concurrent;
using VardyParty.Kernel;

namespace VardyParty.Streaming;

/// <summary>How the unlabeled seed wait of an <see cref="MpHarvestSession"/> answers.</summary>
public enum MpSeedMode
{
    /// <summary>
    /// Rules 5-7: the chip list (or the first chip) releases the seed with every label,
    /// and no waiter holds out more than <see cref="MpHarvestSession.ProbeReadyWait"/>
    /// for probe-ready segments or for a stalled harvest. The LAN resolver queues each
    /// chip as a row.
    /// </summary>
    ChipList,

    /// <summary>
    /// The seed waits for a probe-ready chip and is null when the harvest ends without
    /// one; no deadlines. For callers that read an empty URL as a miss (remote compute).
    /// </summary>
    ProbeReadyOnly
}

/// <summary>
/// Collects streamed <c>/mp</c> chip events so the seed can return as soon as the page
/// lists its chips while later labels wait on the same Chrome session
/// (docs/PROBE_SCHEDULING_RULES.md rules 5-7).
/// </summary>
public sealed class MpHarvestSession
{
    /// <summary>Default for <see cref="ProbeReadyWait"/> (rule 7).</summary>
    public static readonly TimeSpan DefaultProbeReadyWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a finished, fault-free harvest keeps answering for its page from its
    /// own verdicts (a cached miss, or null for a label it never sent) instead of a new
    /// <c>POST /mp</c>. Long enough for one resolution pass to drain its chip rows;
    /// short enough that a later tap asks LocalService again.
    /// </summary>
    public static readonly TimeSpan VerdictMemory = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, M3U8Response> _byLabel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cachedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _listed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _startedAt;
    private readonly List<ITimer> _wakeTimers = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<string> _streams = [];
    private M3U8Response? _seed;
    private string? _error;
    private LocalServiceUnavailableException? _fault;
    private DateTimeOffset _lastProgressAt;
    private DateTimeOffset? _completedAt;
    private ITimer? _stallTimer;
    private volatile bool _done;
    private volatile bool _cancelled;
    private bool _legacy;
    private readonly bool _chipListSeed;

    public MpHarvestSession(TimeProvider? time = null, MpSeedMode mode = MpSeedMode.ChipList, TimeSpan? probeReadyWait = null)
    {
        _time = time ?? TimeProvider.System;
        _startedAt = _time.GetUtcNow();
        _lastProgressAt = _startedAt;
        _chipListSeed = mode == MpSeedMode.ChipList;
        _legacy = !_chipListSeed;
        ProbeReadyWait = probeReadyWait ?? DefaultProbeReadyWait;
    }

    /// <summary>
    /// Longest a waiter holds out (rule 7): the seed counts from the POST; a chip that
    /// only has a playlist counts from its playlist line; a listed chip LocalService has
    /// not sent counts from the harvest's last line (chips event or chip), so a harvest
    /// that makes no progress for this long is treated as stalled (infrastructure).
    /// </summary>
    public TimeSpan ProbeReadyWait { get; }

    public IReadOnlyList<string> Streams
    {
        get
        {
            lock (_gate)
            {
                return _streams.ToList();
            }
        }
    }

    public string? Error => _error;

    /// <summary>
    /// Set when the harvest ended on infrastructure (service unreachable, our
    /// timeout, a 5xx, MP disabled). Waiters without a playlist throw it
    /// instead of seeing <c>null</c>, which would read as "no playlist".
    /// </summary>
    public LocalServiceUnavailableException? Fault => _fault;

    public bool IsCompleted => _completed.Task.IsCompleted;

    public bool IsCancelled => _cancelled;

    public bool HasPlaylist => _byLabel.Values.Any(r => !string.IsNullOrWhiteSpace(r.Url));

    /// <summary>
    /// Keep one Chrome harvest for the same page while it is still running, already
    /// has a playlist, or finished fault-free less than <see cref="VerdictMemory"/>
    /// ago (its chips are verdicts: no re-POST per chip). An older completed miss must
    /// not block the next tap.
    /// </summary>
    public bool CanReuseFor(string? page, string streamUrl) =>
        !_cancelled
        && string.Equals(page, streamUrl, StringComparison.OrdinalIgnoreCase)
        && (!IsCompleted || (HasPlaylist && (_fault is null || WithinVerdictMemory)) || AnswersFromVerdicts);

    public Task WhenCompleted => _completed.Task;

    /// <summary>
    /// Completes once LocalService has listed the page's chips (or sent a first chip),
    /// or the harvest ended. <c>LocalLanPlayService</c> holds its throttle slot until
    /// then (rule 2).
    /// </summary>
    public Task Listed => _listed.Task;

    /// <summary>Completes on the next chip event, completion, failure, cancel or deadline.</summary>
    public Task Changed
    {
        get
        {
            lock (_gate)
                return _changed.Task;
        }
    }

    /// <summary>
    /// A labeled wait for <paramref name="label"/> would answer now, without asking
    /// LocalService for anything: a probe-ready playlist, a miss, a playlist-only chip
    /// past its deadline, a stalled harvest (the wait throws at once), or a finished
    /// harvest. The resolver starts an MP chip row only when this is true while its
    /// page's harvest runs (rule 7), and may do so in the priority window (rule 11).
    /// </summary>
    public bool IsChipReady(string? label)
    {
        if (_cancelled || string.IsNullOrWhiteSpace(label))
            return false;
        if (_done)
            return true;
        var key = label.Trim();
        if (_byLabel.TryGetValue(key, out var cached))
            return IsReadyForWaiter(cached) || PlaylistWaitOver(key);
        return StallWaitOver();
    }

    public void Apply(MpPlayEvent ev)
    {
        switch (ev.Type)
        {
            case "chips":
                lock (_gate)
                {
                    if (ev.Streams is { Count: > 0 })
                    {
                        _streams = ev.Streams.ToList();
                        // Rule 5: the chip list releases the seed; every listed chip
                        // becomes its own row while the harvest carries on.
                        if (!_legacy)
                            _seed ??= ChipList(null);
                        Progress();
                    }
                }

                if (ev.Streams is { Count: > 0 })
                    _listed.TrySetResult();
                Signal();
                break;

            case "chip":
                var response = MpNdjson.ToResponse(ev, Streams);
                if (response is null)
                {
                    return;
                }

                var label = response.SelectedStream ?? ev.Label ?? "";
                if (!string.IsNullOrWhiteSpace(label))
                {
                    var key = label.Trim();
                    _byLabel[key] = response;
                    if (!string.IsNullOrWhiteSpace(response.Url) && !_cachedAt.ContainsKey(key))
                    {
                        _cachedAt[key] = _time.GetUtcNow();
                        // A playlist-only chip turns ready ProbeReadyWait later: wake the
                        // resolver then, not on the next unrelated line (rule 7).
                        if (!MpRewrittenSegment.HasProbeReady(response.RewrittenSegments))
                            ScheduleWake(ProbeReadyWait);
                    }
                }

                lock (_gate)
                {
                    // A probe-ready chip can seed directly. Otherwise the first chip
                    // releases the seed as a chip list (rule 5) - an empty-URL miss
                    // or a playlist still soaking is never the seed's verdict.
                    if (MpRewrittenSegment.HasProbeReady(response.RewrittenSegments))
                        _seed ??= response;
                    else if (!_legacy && !string.IsNullOrWhiteSpace(label))
                        _seed ??= ChipList(label.Trim());
                    Progress();
                }

                if (!string.IsNullOrWhiteSpace(label) || !string.IsNullOrWhiteSpace(response.Url))
                    _listed.TrySetResult();
                Signal();
                break;

            case "error":
                _error = ev.Error;
                if (ev.StatusCode is >= 500)
                {
                    // LocalService's own failure (MpStreamResponse writes 500
                    // "mp play execution failed"). Only a 4xx, e.g. 404 "M3U8 not
                    // captured from MP page.", is the stream's verdict.
                    Fail(new LocalServiceUnavailableException(
                        LocalServiceUnavailableReason.ServiceError,
                        $"POST /mp error {ev.StatusCode}: {ev.Error}"));
                }
                else
                {
                    Complete(failed: true);
                }

                break;

            case "done":
                Complete(failed: false);
                break;
        }
    }

    public void ApplyLegacy(M3U8Response result)
    {
        lock (_gate)
            _legacy = true;

        if (result.Streams is { Count: > 0 })
        {
            Apply(new MpPlayEvent { Type = "chips", Streams = result.Streams, SelectedStream = result.SelectedStream });
        }

        if (!string.IsNullOrWhiteSpace(result.Url) || !string.IsNullOrWhiteSpace(result.SelectedStream))
        {
            Apply(new MpPlayEvent
            {
                Type = "chip",
                Label = result.SelectedStream,
                Url = result.Url,
                RequestHeaders = result.RequestHeaders,
                RewrittenSegments = result.RewrittenSegments,
                Streams = result.Streams
            });
        }

        Complete(failed: string.IsNullOrWhiteSpace(result.Url));
    }

    /// <summary>
    /// <paramref name="label"/> null is the seed: it answers with the first probe-ready
    /// chip, or with a chip list (no URL) once LocalService lists chips or sends its
    /// first chip, or with whatever the harvest has after <see cref="ProbeReadyWait"/>.
    /// A finished harvest with no playlist at all answers the seed with null: every
    /// chip already has its verdict, so queueing them would only re-ask (rule 6).
    /// A label answers with its playlist once it is probe-ready, once the harvest ends,
    /// or <see cref="ProbeReadyWait"/> after its playlist-only line arrived. A label
    /// LocalService has not sent throws a retryable <see cref="LocalServiceUnavailableException"/>
    /// once the harvest has made no progress for <see cref="ProbeReadyWait"/> (rule 7:
    /// Deferred, never Failed).
    /// </summary>
    public async Task<M3U8Response?> WaitForAsync(string? label, CancellationToken cancellationToken)
    {
        ThrowIfCancelled(cancellationToken);
        var key = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        while (true)
        {
            var changed = Changed;
            ThrowIfCancelled(cancellationToken);

            TimeSpan wait;
            if (key is null)
            {
                // A probe-ready chip beats a bare chip list when both are in by now,
                // and a harvest that already failed is the seed's answer, not a list
                // of chips that would each hit the same fault.
                if (FirstProbeReady() is { } ready)
                    return ready;
                if (_done && _fault is not null)
                    throw FaultForWaiter();
                if (_done && _chipListSeed && !HasPlaylist)
                    return null;

                lock (_gate)
                {
                    if (_seed is not null)
                        return _seed;
                }

                if (_done)
                    return _chipListSeed ? SeedFallback() : null;

                wait = _chipListSeed ? ProbeReadyWait - (_time.GetUtcNow() - _startedAt) : Timeout.InfiniteTimeSpan;
                if (_chipListSeed && wait <= TimeSpan.Zero)
                    return SeedFallback() ?? throw SeedTimedOut();
            }
            else
            {
                _byLabel.TryGetValue(key, out var cached);
                if (cached is not null && IsReadyForWaiter(cached))
                    return cached;
                if (_done)
                {
                    if (cached is not null)
                        return cached;
                    if (_fault is not null)
                        throw FaultForWaiter();
                    return null;
                }

                wait = Timeout.InfiniteTimeSpan;
                if (_chipListSeed && cached is not null && _cachedAt.TryGetValue(key, out var at))
                {
                    wait = ProbeReadyWait - (_time.GetUtcNow() - at);
                    if (wait <= TimeSpan.Zero)
                        return cached;
                }
                else if (_chipListSeed && cached is null)
                {
                    wait = ProbeReadyWait - (_time.GetUtcNow() - LastProgressAt);
                    if (wait <= TimeSpan.Zero)
                        throw ChipTimedOut(key);
                }
            }

            try
            {
                await changed.WaitAsync(wait, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Deadline reached: the loop answers with what the harvest has.
            }
        }
    }

    /// <summary>
    /// The harvest was cancelled (every caller that wanted it went away). Waiters throw
    /// instead of observing a failed playlist, so the caller does not invalidate discovery.
    /// </summary>
    public void Cancel()
    {
        _cancelled = true;
        _done = true;
        MarkCompleted();
    }

    private void ThrowIfCancelled(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cancelled)
            throw new OperationCanceledException(cancellationToken);
    }

    public void Complete(bool failed, string? error = null)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            _error = error;
        }

        _done = true;
        MarkCompleted();
    }

    /// <summary>
    /// The harvest ended on infrastructure, not on the stream. Labels that
    /// already have a playlist still get it; every other waiter throws
    /// <paramref name="fault"/> so the resolver defers the row instead of
    /// marking it Failed.
    /// </summary>
    public void Fail(LocalServiceUnavailableException fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (_done)
            return;

        _fault = fault;
        _error = fault.Message;
        _done = true;
        MarkCompleted();
    }

    private void MarkCompleted()
    {
        List<ITimer> timers;
        lock (_gate)
        {
            _completedAt ??= _time.GetUtcNow();
            timers = _wakeTimers.ToList();
            _wakeTimers.Clear();
            if (_stallTimer is not null)
                timers.Add(_stallTimer);
            _stallTimer = null;
        }

        foreach (var timer in timers)
            timer.Dispose();
        _completed.TrySetResult();
        _listed.TrySetResult();
        Signal();
    }

    private void Signal()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        old.TrySetResult();
    }

    /// <summary>A harvest line arrived: restart the stall clock (caller holds <see cref="_gate"/>).</summary>
    private void Progress()
    {
        _lastProgressAt = _time.GetUtcNow();
        if (!_chipListSeed || _done)
            return;
        _stallTimer?.Dispose();
        _stallTimer = _time.CreateTimer(static s => ((MpHarvestSession)s!).Signal(), this, ProbeReadyWait, Timeout.InfiniteTimeSpan);
    }

    private void ScheduleWake(TimeSpan due)
    {
        if (!_chipListSeed)
            return;
        lock (_gate)
        {
            if (_done)
                return;
            _wakeTimers.Add(_time.CreateTimer(static s => ((MpHarvestSession)s!).Signal(), this, due, Timeout.InfiniteTimeSpan));
        }
    }

    private DateTimeOffset LastProgressAt
    {
        get
        {
            lock (_gate)
                return _lastProgressAt;
        }
    }

    private bool AnswersFromVerdicts => _chipListSeed && _fault is null && WithinVerdictMemory;

    private bool WithinVerdictMemory => _completedAt is { } at && _time.GetUtcNow() - at < VerdictMemory;

    /// <summary>
    /// The fault for a waiter on a finished harvest. While the harvest is reused for
    /// its page (it has a playlist), asking again at once would only replay this
    /// fault, so the row goes straight to Deferred and the orchestrator's re-queue asks
    /// again later; with no playlist the harvest is not reused and a retry re-POSTs.
    /// </summary>
    private LocalServiceUnavailableException FaultForWaiter()
    {
        var fault = _fault!;
        return HasPlaylist
            ? new LocalServiceUnavailableException(fault.Reason, fault.Message, fault) { FromFinishedHarvest = true }
            : fault;
    }

    private M3U8Response? FirstProbeReady()
    {
        lock (_gate)
        {
            if (_seed is not null && MpRewrittenSegment.HasProbeReady(_seed.RewrittenSegments))
                return _seed;
        }

        foreach (var label in Streams)
        {
            if (_byLabel.TryGetValue(label, out var r) && MpRewrittenSegment.HasProbeReady(r.RewrittenSegments))
                return r;
        }

        return _byLabel.Values.FirstOrDefault(r => MpRewrittenSegment.HasProbeReady(r.RewrittenSegments));
    }

    private bool PlaylistWaitOver(string key) =>
        _chipListSeed
        && _cachedAt.TryGetValue(key, out var at) && _time.GetUtcNow() - at >= ProbeReadyWait;

    private bool StallWaitOver() =>
        _chipListSeed && _time.GetUtcNow() - LastProgressAt >= ProbeReadyWait;

    /// <summary>
    /// Seed deadline passed with nothing listed while the harvest still runs: we know
    /// nothing about the page yet, so it is infrastructure (the row is retried, then
    /// Deferred and re-queued, and the next ask picks up the chip list), never a miss.
    /// </summary>
    private LocalServiceUnavailableException SeedTimedOut() =>
        new(LocalServiceUnavailableReason.CallTimeout,
            $"POST /mp listed no chip within {ProbeReadyWait.TotalSeconds:F0}s");

    /// <summary>
    /// A label LocalService has not sent while the harvest made no progress for
    /// <see cref="ProbeReadyWait"/>: the harvest is stalled, which says nothing about
    /// the chip, so the row is retried, then Deferred (rule 7, rule 13).
    /// </summary>
    private LocalServiceUnavailableException ChipTimedOut(string label) =>
        new(LocalServiceUnavailableReason.CallTimeout,
            $"POST /mp sent nothing for {ProbeReadyWait.TotalSeconds:F0}s; chip {label} not harvested yet");

    /// <summary>Seed answer once the harvest is over or the seed deadline passed (rule 6).</summary>
    private M3U8Response? SeedFallback()
    {
        lock (_gate)
        {
            if (_seed is not null)
                return _seed;
        }

        if (Streams.Count == 0)
            return null;

        // Prefer a chip that already has a playlist as the first row.
        var withPlaylist = Streams.FirstOrDefault(s =>
            _byLabel.TryGetValue(s, out var r) && !string.IsNullOrWhiteSpace(r.Url));
        lock (_gate)
            return ChipList(withPlaylist);
    }

    /// <summary>A seed answer with no URL: the page's chips, each to be queued as its own row.</summary>
    private M3U8Response ChipList(string? first)
    {
        var streams = _streams.Count > 0 ? _streams.ToList() : first is null ? [] : [first];
        return new M3U8Response
        {
            Url = "",
            Streams = streams,
            SelectedStream = first
        };
    }

    /// <summary>
    /// Named waiters accept a definitive miss (no URL) or a playlist that
    /// already carries probe-shaped rewritten segment samples (ReleaseChipThenSoak
    /// emits playlist-first then an enriched line - answering on the early line
    /// fails sibling health), unless <see cref="ProbeReadyWait"/> has passed.
    /// </summary>
    private static bool IsReadyForWaiter(M3U8Response response) =>
        string.IsNullOrWhiteSpace(response.Url)
        || MpRewrittenSegment.HasProbeReady(response.RewrittenSegments);
}
