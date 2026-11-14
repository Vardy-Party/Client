using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using VardyParty.LocalService.V2;
using VardyParty.Playback;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming.Tests.Timeline;

/// <summary>
/// Runs a <see cref="ProbeScenario"/> against the REAL scheduling code - StreamResolver
/// (FB window, MP lane, seed re-queue, sibling queueing), LocalLanPlayService (throttle slots,
/// <c>/mp</c> NDJSON harvest, MpHarvestSession seed rules), ResolverRaceThrottle,
/// StreamTestOrderPolicy (try-order) and StreamSwitchingService (channel numbers;
/// first come, first served, as in the orchestrator), TopTierHeadStart (rule 1) and
/// FirstPlayOpenStrategy (how first play opens, rule 12; when the priority window holds,
/// rule 11). Only LocalService (HTTP) and the CDN health check are scripted, on a
/// <see cref="MilestoneClock"/>: the run jumps from one milestone to the next, so a 111s
/// harvest runs in milliseconds.
/// Modelled like the orchestrator: the top-tier head start around run 1; the start
/// <c>/play</c> when the probed link cannot be reused (with the backup warmed beside it);
/// a prewarmer that warms upcoming rows that need it, one at a time, from first play; and
/// the rule-6 priority window; optionally the orchestrator's re-queue pass of the Deferred
/// rows (<see cref="ProbeScenario.ThenReResolveDeferredAfter"/>). Not modelled: the player itself (first play = the moment the player would open) and the real-time
/// <c>/play</c> timeout. The <c>/mp</c> harvest timeout and the 30s seed/chip deadlines run
/// on this clock (<see cref="LocalLanPlayService.Time"/>).
/// Between milestones the harness waits for the thread pool to go quiet. If it does
/// not within <c>MaxSettle</c> the run fails (with the timeline so far) rather than
/// advance the clock early and pass or fail for the wrong reason.
/// </summary>
internal static class ProbeTimelineHarness
{
    public static readonly DateTimeOffset Start = new(2026, 11, 12, 20, 12, 25, TimeSpan.Zero);

    public static Task<ProbeTimeline> RunAsync(ProbeScenario scenario) =>
        Task.Run(() => new Run(scenario).ExecuteAsync());

    internal static string FbPage(string id) => $"https://fb.example.test/{id}";
    internal static string MpPage(string id) => $"https://mp.example.test/{id}";
    internal static string FbPlaylist(string id) => $"https://cdn.example.test/{id}.m3u8";
    internal static string ChipPlaylist(string id, string label) => $"https://cdn.example.test/{id}/{Uri.EscapeDataString(label)}.m3u8";

    private sealed class Run
    {
        private const string Endpoint = "http://192.168.1.251:5019";
        private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(10);
        private static readonly TimeSpan MaxSettle = TimeSpan.FromSeconds(5);

        private readonly ProbeScenario _scenario;
        private readonly MilestoneClock _clock = new(Start);
        private readonly ProbeTimeline _tl;
        private readonly ResolverRaceThrottle _throttle = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _chipChecks = new(StringComparer.Ordinal);
        private readonly StreamSwitchingService _pool = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationTokenSource _switchCts = new();
        private Task? _consumeAfterSwitch;
        private ITimer? _switchTimer;
        private readonly Dictionary<string, RowSpec> _rowsById = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<StreamModel, string> _keyByRef = new(ReferenceEqualityComparer.Instance);
        private readonly object _gate = new();
        private List<StreamModel> _ordered = new();
        private bool _hasPlayed;
        private IDisposable? _priorityHold;
        private ITimer? _priorityTimer;
        private double? _priorityHoldFrom;
        private bool _windowOpen;
        private LocalLanPlayService? _lan;
        private TopTierHeadStart? _headStart;
        private Func<EnrichedStream, StreamTier>? TierOf => _headStart?.Tiers is { } tiers ? tiers.TierOf : null;
        // The warm cache, as the prewarmer fills it: row -> when its warm /play answered.
        private readonly Dictionary<EnrichedStream, double> _warm = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<EnrichedStream> _warmInFlight = new(ReferenceEqualityComparer.Instance);
        private EnrichedStream? _playing;
        private long _activity;
        private int _idleWorkers = 1;
        // Resolver lanes (from its logs and yields): FB window of 2, MP lane of 1.
        private int _fbTotal;
        private int _mpTotal;
        private int _fbStarted;
        private int _mpStarted;
        private int _fbDone;
        private int _mpDone;
        private int _plays;
        private int _mpOpen;
        private int _healthOpen;
        // Rows that ended Deferred in run 1 (the orchestrator's re-queue list), and /play calls per row.
        private readonly List<StreamModel> _deferred = new();
        private readonly Dictionary<string, int> _playCalls = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _lastPool = new();

        public Run(ProbeScenario scenario)
        {
            _scenario = scenario;
            _tl = new ProbeTimeline { Scenario = scenario };
            // [probe-timing] ms count from the scenario's t=0, as from the orchestrator's session start.
            _throttle.Timing.BeginSession(_clock);
            foreach (var row in scenario.Rows)
                _rowsById[row.Id] = row;
        }

        private double T => _clock.T;

        private void Bump() => Interlocked.Increment(ref _activity);

        private void Note(string line)
        {
            Bump();
            lock (_tl)
                _tl.Log.Add((T, line));
        }

        public async Task<ProbeTimeline> ExecuteAsync()
        {
            // Busy pool workers when nothing of ours runs: this loop plus whatever the
            // test host keeps blocked. Settle waits for the count to come back to it.
            // Sampled for a while: a test-host thread that happens to be busy at the
            // first sample would set the bar too high, and the first milestone could
            // then pass while the resolver is still starting its first probes.
            _idleWorkers = int.MaxValue;
            var sample = Stopwatch.StartNew();
            var sampleSpin = new SpinWait();
            while (sample.ElapsedMilliseconds < 30)
            {
                sampleSpin.SpinOnce(sleep1Threshold: 10);
                _idleWorkers = Math.Min(_idleWorkers, BusyWorkers());
            }

            var firstRun = _scenario.Rows.Where(r => r.Run == 1).ToList();
            var catalog = firstRun.Select(BuildStream).ToList();
            for (var i = 0; i < catalog.Count; i++)
                _keyByRef[catalog[i]] = firstRun[i].Id;

            var recommendations = _scenario.Recommended.Count == 0
                ? null
                : new RecommendationResponse
                {
                    HasData = true,
                    Recommended = _scenario.Recommended
                        .Select(r => new RecommendationItem { Url = PageOf(_rowsById[r.Id]), Confidence = r.Confidence })
                        .ToList()
                };
            var order = StreamTestOrderPolicy.Build(
                recommendations,
                catalog.Count,
                (url, _) => catalog.FindIndex(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)),
                i => catalog[i],
                sessionSalt: 0);
            _ordered = order.Select(i => catalog[i]).ToList();
            _mpTotal = _ordered.Count(MpPageUrl.IsV2Stream);
            _fbTotal = _ordered.Count - _mpTotal;
            Note("try-order: " + string.Join(", ", _ordered.Select(s => _keyByRef[s])));

            var lan = new LocalLanPlayService(
                new HttpClient(new ScriptedLocalService(this)) { Timeout = Timeout.InfiniteTimeSpan },
                Options.Create(new GamesApiSettings
                {
                    CallTimeoutSeconds = 5,
                    MaxRetries = 0,
                    RefreshSchedule = 60,
                    M3U8CallTimeoutSeconds = 5
                }),
                Array.Empty<IPlaybackTransportPlugin>(),
                new RecordingLogger<LocalLanPlayService>(this),
                raceThrottle: _throttle);
            lan.DiscoverOverrideForTests = _ => Task.FromResult<string?>(Endpoint);
            lan.DelayAsync = (d, ct) => Task.Delay(d, _clock, ct);
            lan.Clock = _clock.GetUtcNow;
            lan.Time = _clock;
            lan.EndpointsForTests.Remember(Endpoint, DateTimeOffset.UtcNow);
            _lan = lan;

            var resolver = new StreamResolver(
                new ScriptedHealth(this),
                new RecordingLan(this, lan),
                new V2PlaybackTransportPlugin(),
                new RecordingLogger<StreamResolver>(this),
                _throttle)
            {
                DelayAsync = (d, ct) => Task.Delay(d, _clock, ct)
            };

            var firstToken = _scenario.SwitchAt is null
                ? _cts.Token
                : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _switchCts.Token).Token;

            // Rule 1: the orchestrator wraps the first pass in the head start (High, then Medium).
            var (topTier, mediumTier) = TopTierHeadStart.Select(recommendations, _ordered);
            _headStart = _throttle.BeginTopTierHeadStart(topTier, mediumTier, _clock);
            if (_headStart is not null)
            {
                _tl.HeadStart = (0, TopTierHeadStart.Duration.TotalSeconds,
                    topTier.Select(r => _keyByRef[r]).ToList(), mediumTier.Select(r => _keyByRef[r]).ToList());
                Note($"head start {TopTierHeadStart.Duration.TotalSeconds}s: High {string.Join(", ", topTier.Select(r => _keyByRef[r]))}; Medium {string.Join(", ", mediumTier.Select(r => _keyByRef[r]))}");
                var headStart = _headStart;
                _ = headStart.Elapsed.ContinueWith(
                    _ => { if (T >= TopTierHeadStart.Duration.TotalSeconds - 1e-9) Note("head start ends"); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            var consume = Task.Run(async () =>
            {
                try
                {
                    await foreach (var row in resolver
                                       .ResolveStreamsIncrementallyAsync(_ordered, 2, firstToken)
                                       .ConfigureAwait(false))
                    {
                        Accept(row);
                    }
                }
                catch (OperationCanceledException) when (_switchCts.IsCancellationRequested)
                {
                    Note("run 1 cancelled (match switch)");
                    return;
                }
                finally
                {
                    _headStart?.Dispose();
                }

                // The orchestrator's re-queue pass: the Deferred rows again, same session (the
                // tiers and the Medium cap still apply), no head start.
                if (_scenario.ReResolveDeferredAfter is not { } after)
                    return;
                List<StreamModel> retry;
                lock (_gate)
                {
                    retry = _deferred.ToList();
                    _deferred.Clear();
                }

                if (retry.Count == 0)
                    return;
                var wait = _scenario.CdnRecheckFromPolicy
                    ? CdnReachabilityPolicy.RecheckDelay(0, noBackup: _pool.GetHealthyStreams().Count < 2)
                    : TimeSpan.FromSeconds(after);
                Note($"re-queue wait {wait.TotalSeconds:F0}s");
                await Task.Delay(wait, _clock, _cts.Token).ConfigureAwait(false);
                Note($"re-queue pass: {string.Join(", ", retry.Select(KeyOf))}");
                Interlocked.Add(ref _mpTotal, retry.Count(MpPageUrl.IsV2Stream));
                Interlocked.Add(ref _fbTotal, retry.Count(r => !MpPageUrl.IsV2Stream(r)));
                await foreach (var row in resolver
                                   .ResolveStreamsIncrementallyAsync(retry, 2, _cts.Token)
                                   .ConfigureAwait(false))
                {
                    Accept(row);
                }
            });

            if (_scenario.SwitchAt is { } switchAt)
            {
                // A match switch: cancel run 1, then start run 2 on the same LocalLanPlayService
                // and throttle with the rows added after SwitchAt.
                var second = _scenario.Rows.Where(r => r.Run == 2).ToList();
                var secondStreams = second.Select(BuildStream).ToList();
                for (var i = 0; i < secondStreams.Count; i++)
                    _keyByRef[secondStreams[i]] = second[i].Id;
                _switchTimer = _clock.CreateTimer(_ =>
                {
                    Note("match switch: cancel run 1, start run 2");
                    _switchCts.Cancel();
                    _consumeAfterSwitch = Task.Run(async () =>
                    {
                        await foreach (var row in resolver
                                           .ResolveStreamsIncrementallyAsync(secondStreams, 2, _cts.Token)
                                           .ConfigureAwait(false))
                        {
                            Accept(row);
                        }
                    });
                }, null, TimeSpan.FromSeconds(switchAt), Timeout.InfiniteTimeSpan);
            }

            bool AllDone() => consume.IsCompleted
                              && (_scenario.SwitchAt is null || _consumeAfterSwitch is { IsCompleted: true });

            var horizonHit = false;
            while (true)
            {
                Settle();
                TakeSnapshot();
                if (AllDone())
                    break;

                var next = _clock.NextDue;
                if (next is null)
                {
                    Settle(TimeSpan.FromMilliseconds(50));
                    if (AllDone())
                        break;
                    if (_clock.NextDue is null)
                        throw new InvalidOperationException(
                            $"Stalled at {ProbeTimeline.Fmt(T)}: nothing scheduled and the resolver has not finished.\n{_tl.Render()}");
                    continue;
                }

                if ((next.Value - _clock.Start).TotalSeconds > _scenario.Horizon)
                {
                    horizonHit = true;
                    break;
                }

                _clock.AdvanceToNext();
            }

            _tl.EndedAt = horizonHit ? _scenario.Horizon : T;
            _cts.Cancel();
            try
            {
                await consume.ConfigureAwait(false);
                if (_consumeAfterSwitch is not null)
                    await _consumeAfterSwitch.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (horizonHit)
            {
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Resolver faulted: {ex.Message}\n{_tl.Render()}", ex);
            }

            _priorityTimer?.Dispose();
            _switchTimer?.Dispose();
            _throttle.SetBackupWanted(false);
            lock (_gate)
                EndPriorityHold();
            _headStart?.Dispose();
            if (_headStart is { } hs)
            {
                // Rows the tiers hold at the end: the recommended rows plus the chips that
                // inherited their page's tier (rule 1).
                foreach (var (row, high) in hs.Tiers.HighRows.Select(r => (r, true)).Concat(hs.Tiers.MediumRows.Select(r => (r, false))))
                {
                    var key = KeyOf(row);
                    (high ? _tl.HighAtEnd : _tl.MediumAtEnd).Add(key);
                    _tl.TierCallKeys.Add(key);
                    if (!string.IsNullOrWhiteSpace(row.PlayerStream))
                        _tl.TierCallKeys.Add($"{RowIdOf(row.Url)}:{row.PlayerStream.Trim()}");
                }
            }

            lock (_gate)
            {
                _tl.Pool.Clear();
                _tl.Pool.AddRange(_pool.GetHealthyStreams().Select(e => KeyOf(e.Stream)));
            }

            return _tl;
        }

        /// <summary>
        /// Waits until every continuation the last milestone released has run: no
        /// queued thread-pool work, no more busy workers than the idle baseline, and no new harness
        /// activity, for <see cref="Quiet"/>. (Counting busy workers matters: a first
        /// call can spend tens of ms in JIT with nothing queued and nothing logged.)
        /// Polls with <see cref="SpinWait"/> (yields, then 1ms sleeps), not a hot spin, so
        /// a parallel test assembly is not starved of CPU. Fails the run when the pool
        /// does not go quiet within <see cref="MaxSettle"/>: advancing the clock then
        /// could fire the next milestone before earlier continuations ran.
        /// </summary>
        private void Settle(TimeSpan? quiet = null)
        {
            var window = quiet ?? Quiet;
            var sw = Stopwatch.StartNew();
            var spin = new SpinWait();
            var last = Interlocked.Read(ref _activity);
            var quietFrom = sw.Elapsed;
            while (sw.Elapsed < MaxSettle)
            {
                spin.SpinOnce(sleep1Threshold: 10);
                var now = Interlocked.Read(ref _activity);
                var busy = BusyWorkers();

                // Work left over from an earlier run (or the test host) can make the
                // starting baseline too high; once it drains, the lower count is idle.
                if (busy < _idleWorkers)
                    _idleWorkers = busy;
                if (now != last || ThreadPool.PendingWorkItemCount > 0 || busy > _idleWorkers)
                {
                    last = now;
                    quietFrom = sw.Elapsed;
                    spin.Reset();
                    continue;
                }

                if (sw.Elapsed - quietFrom >= window)
                {
                    _tl.SettleCount++;
                    _tl.SettleTotalMs += sw.Elapsed.TotalMilliseconds;
                    _tl.SettleMaxMs = Math.Max(_tl.SettleMaxMs, sw.Elapsed.TotalMilliseconds);
                    return;
                }
            }

            throw new InvalidOperationException(
                $"Harness settle timed out after {MaxSettle.TotalSeconds:0}s at {ProbeTimeline.Fmt(T)} " +
                $"(busy workers {BusyWorkers()}, idle bar {_idleWorkers}, queued work items {ThreadPool.PendingWorkItemCount}). " +
                $"Advancing the clock now would make the timeline nondeterministic.\n{_tl.Render()}");
        }

        private static int BusyWorkers()
        {
            ThreadPool.GetMaxThreads(out var max, out _);
            ThreadPool.GetAvailableThreads(out var available, out _);
            return max - available;
        }

        private void TakeSnapshot()
        {
            lock (_tl)
            {
                var inFlight = _tl.Calls.Where(c => c.End is null).Select(c => c.Key).ToList();
                var snap = new Snapshot(
                    T,
                    _throttle.SlotsInUse,
                    Volatile.Read(ref _plays),
                    Volatile.Read(ref _mpOpen),
                    Volatile.Read(ref _healthOpen),
                    Math.Max(0, Volatile.Read(ref _fbTotal) - Volatile.Read(ref _fbStarted)),
                    Math.Max(0, Volatile.Read(ref _mpTotal) - Volatile.Read(ref _mpStarted)),
                    Volatile.Read(ref _fbStarted) - Volatile.Read(ref _fbDone),
                    Volatile.Read(ref _mpStarted) - Volatile.Read(ref _mpDone),
                    _throttle.RaceInFlight,
                    inFlight,
                    _throttle.CurrentHeadStart?.FindingHeld == true || _throttle.CurrentTiers?.RowsHeld == true);
                if (_tl.Snapshots.Count > 0 && Math.Abs(_tl.Snapshots[^1].At - snap.At) < 1e-9)
                    _tl.Snapshots[^1] = snap;
                else
                    _tl.Snapshots.Add(snap);
            }
        }

        // ---- the orchestrator's Accept/JoinPool: first come, first served (rule 9) ----

        private void Accept(EnrichedStream row)
        {
            lock (_gate)
            {
                var key = KeyOf(row.Stream);
                lock (_tl)
                    _tl.Results.Add(new RowResult(key, T, row.Status, row.ErrorMessage));
                Note($"yield {key}: {row.Status}{(row.ErrorMessage is { } e ? $" ({e})" : "")}");

                Interlocked.Increment(ref MpPageUrl.IsV2Stream(row.Stream) ? ref _mpDone : ref _fbDone);
                if (row.Status == StreamResolutionStatus.Healthy)
                    JoinPool(row);
                else if (row.Status == StreamResolutionStatus.Deferred)
                    _deferred.Add(row.Stream);
            }
        }

        private void JoinPool(EnrichedStream row)
        {
            _pool.AddHealthyStream(row);
            var key = KeyOf(row.Stream);
            // Rule 11 backup push, as the orchestrator: a row plays and no other is in the pool yet.
            _throttle.SetBackupWanted(_pool.GetHealthyStreams().Count < 2);
            // Channel numbers: the pool before this row must be a prefix of the pool after it.
            var pool = _pool.GetHealthyStreams().Select(e => KeyOf(e.Stream)).ToList();
            if (!pool.Take(_lastPool.Count).SequenceEqual(_lastPool))
            {
                _tl.Renumbered++;
                Note($"RENUMBERED: {string.Join(", ", _lastPool)} -> {string.Join(", ", pool)}");
            }

            _lastPool = pool;
            Note($"pool +{key} (#{_pool.GetHealthyStreams().Count})");
            if (_hasPlayed)
            {
                AfterPoolChange();
                return;
            }

            // StartPlaybackChain: the priority window begins, then RaceStartAsync plans the open.
            _hasPlayed = true;
            _playing = row;
            // Rule 11: Next and the prewarmer follow channel order.
            if (_scenario.PriorityWindowAfterFirstPlay)
            {
                _windowOpen = true;
                _tl.PriorityWindow = (T, T + ResolverRaceThrottle.PlaybackPriorityTimeout.TotalSeconds);
                Note($"priority window ({ResolverRaceThrottle.PlaybackPriorityTimeout.TotalSeconds}s): holds only while a backup needs warming");
                _priorityTimer = _clock.CreateTimer(_ =>
                {
                    lock (_gate)
                    {
                        _windowOpen = false;
                        EndPriorityHold();
                        Note("priority window ends");
                    }
                }, null, ResolverRaceThrottle.PlaybackPriorityTimeout, Timeout.InfiniteTimeSpan);
                UpdatePriorityHold();
            }

            var plan = FirstPlayOpenStrategy.PlanStart(
                row, _pool.GetHealthyStreams(), _clock.GetUtcNow(),
                r => _warm.ContainsKey(r) || _warmInFlight.Contains(r));
            _tl.FirstPlayKind = plan.Kind;
            if (plan.Kind != FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst)
            {
                FirstPlayNow(row, plan.Reason);
                return;
            }

            // The probed link cannot be reused: one fresh /play for the start row (it takes
            // the next free slot ahead of finding; no drain), the backup warmed beside it.
            // StartPlaybackChain pauses new finding starts at once (before the resolver's next fill).
            Note($"start /play {key} ({plan.Reason})");
            var started = T;
            var race = _throttle.BeginRace();
            if (plan.PartnerToWarm is { } partner)
                StartWarm(partner);
            _ = Task.Run(async () =>
            {
                using var startHold = race;
                using var leg = _throttle.MarkRaceLeg();
                try
                {
                    var response = await _lan!.ResolveM3U8UrlAsync(row.Stream.Url, null, "v1", "fb", _cts.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _tl.FreshStartPlay = (started, T);
                        if (!string.IsNullOrWhiteSpace(response?.Url))
                            FirstPlayNow(row, "fresh /play answered");
                        else
                            Note($"start /play {key} returned no playlist");
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        private void FirstPlayNow(EnrichedStream row, string why)
        {
            var key = KeyOf(row.Stream);
            _tl.FirstPlay = (T, key);
            Note($"FIRST PLAY on {key} ({_tl.FirstPlayKind}: {why})");
            AfterPoolChange();
        }

        // PlayStreamAsync starts the prewarmer once the player opens; pool changes kick it.
        private void AfterPoolChange()
        {
            if (_playing is null)
                return;
            if (_tl.FirstPlay is not null)
                KickPrewarm();
            TrackBackup();
            UpdatePriorityHold();
        }

        // Where Next would go from the playing row: the next channel in order (rule 11).
        private EnrichedStream? CurrentBackup() => _pool.GetUpcoming(1).FirstOrDefault();

        private bool NeedsWarm(EnrichedStream row) =>
            PlaybackUrlReusePolicy.ShouldPrewarm(row) && !_warm.ContainsKey(row);

        // The next channel can change when a row is appended, dropped, or skipped.
        private void TrackBackup()
        {
            if (!ReferenceEquals(_pool.GetCurrentStream(), _playing))
            {
                // Nobody presses Next in the harness: a change of the playing row is the scheduler's.
                _tl.Switches++;
                Note($"SWITCH: playing {KeyOf(_pool.GetCurrentStream()?.Stream ?? _playing!.Stream)}, expected {KeyOf(_playing!.Stream)}");
                _playing = _pool.GetCurrentStream();
                return;
            }

            if (CurrentBackup() is not { } backup)
                return;
            var key = KeyOf(backup.Stream);
            if (_tl.Backup?.Key != key)
            {
                Note(_tl.Backup is { } was ? $"backup: {key} (replaces {was.Key})" : $"backup: {key}");
                _tl.Backup = (T, key);
                _tl.Backups.Add((T, key));
                _tl.BackupReadyAt = null;
            }

            if (_tl.BackupReadyAt is null && !NeedsWarm(backup))
            {
                _tl.BackupReadyAt = T;
                _tl.FirstBackupReadyAt ??= T;
                Note($"backup {key} ready");
            }
        }

        // The prewarmer: one warm /play at a time for the next PrewarmDepth rows that need it.
        private void KickPrewarm()
        {
            if (_warmInFlight.Count > 0)
                return;
            var target = _pool.GetUpcoming(PlaybackUrlReusePolicy.PrewarmDepth).FirstOrDefault(NeedsWarm);
            if (target is not null)
                StartWarm(target);
        }

        private void StartWarm(EnrichedStream row)
        {
            if (!_warmInFlight.Add(row))
                return;
            var key = KeyOf(row.Stream);
            var from = T;
            Note($"warm /play {key}");
            _ = Task.Run(async () =>
            {
                // MarkRaceLeg: the warm takes a slot through the priority / race hold.
                using var leg = _throttle.MarkRaceLeg();
                try
                {
                    var response = await _lan!.ResolveM3U8UrlAsync(row.Stream.Url, null, "v1", "fb", _cts.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _warmInFlight.Remove(row);
                        _tl.Warms.Add((key, from, T));
                        if (!string.IsNullOrWhiteSpace(response?.Url))
                        {
                            _warm[row] = T;
                            Note($"warm {key} ready");
                        }

                        AfterPoolChange();
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        // Rule 11: inside the window, hold finding only while a backup needs warming.
        private void UpdatePriorityHold()
        {
            if (!_windowOpen)
                return;
            var now = _clock.GetUtcNow();
            var wanted = FirstPlayOpenStrategy.PlaybackPriorityHolds(
                FirstPlayOpenStrategy.CountBackupsNeedingWarm(
                    _pool.GetUpcoming(PlaybackUrlReusePolicy.PrewarmDepth), now,
                    r => _warm.TryGetValue(r, out var at) ? (now + TimeSpan.FromMinutes(10), _clock.Start + TimeSpan.FromSeconds(at)) : null,
                    _ => false));
            if (wanted && _priorityHold is null)
            {
                _priorityHold = _throttle.BeginPlaybackPriority();
                _priorityHoldFrom = T;
                Note("priority hold: a backup needs warming");
            }
            else if (!wanted)
            {
                EndPriorityHold();
            }
        }

        private void EndPriorityHold()
        {
            if (_priorityHold is null)
                return;
            _priorityHold.Dispose();
            _priorityHold = null;
            _tl.PriorityHolds.Add((_priorityHoldFrom ?? T, T));
            Note("priority hold ends");
        }

        // ---- identity ----

        private static StreamModel BuildStream(RowSpec row) => row.IsMp
            ? new StreamModel { Url = MpPage(row.Id), Channel = "", ResolutionStrategy = "v2", Source = "mp" }
            : new StreamModel { Url = FbPage(row.Id), Channel = row.Id, ResolutionStrategy = "v1", Source = "fb" };

        private static string PageOf(RowSpec row) => row.IsMp ? MpPage(row.Id) : FbPage(row.Id);

        private static string RowIdOf(string url) => url[(url.LastIndexOf('/') + 1)..];

        private string KeyOf(StreamModel stream)
        {
            lock (_keyByRef)
            {
                if (_keyByRef.TryGetValue(stream, out var key))
                    return key;
            }

            var id = RowIdOf(stream.Url);
            return string.IsNullOrWhiteSpace(stream.PlayerStream) ? id : $"{id}:{stream.PlayerStream.Trim()}";
        }

        private RowSpec RowFor(string page) =>
            _rowsById.TryGetValue(RowIdOf(page), out var row)
                ? row
                : throw new InvalidOperationException($"No scripted row for {page}");

        // ---- recording ----

        internal ProbeCall BeginCall(string url, string? label)
        {
            var row = RowFor(url);
            var call = new ProbeCall
            {
                RowId = row.Id,
                Key = string.IsNullOrWhiteSpace(label) ? row.Id : $"{row.Id}:{label.Trim()}",
                Kind = !row.IsMp ? ProbeKind.Play : string.IsNullOrWhiteSpace(label) ? ProbeKind.MpSeed : ProbeKind.MpChip,
                Start = T
            };
            lock (_tl)
                _tl.Calls.Add(call);
            Note($"probe start {call.Key} ({call.Kind})");
            return call;
        }

        internal void EndCall(ProbeCall call, string outcome)
        {
            call.End = T;
            call.Outcome = outcome;
            Note($"probe end {call.Key}: {outcome}");
        }

        internal void OnLog(LogLevel level, IReadOnlyList<KeyValuePair<string, object?>>? state, string message)
        {
            Bump();
            var template = state?.FirstOrDefault(kv => kv.Key == "{OriginalFormat}").Value as string;
            object? Value(string name) => state?.FirstOrDefault(kv => kv.Key == name).Value;
            if (message.StartsWith(ProbeTimingLog.Prefix, StringComparison.Ordinal))
            {
                lock (_tl)
                    _tl.ProbeTimingLines.Add(message);
            }
            else if (template is not null && template.StartsWith("[StreamResolver] MP chip", StringComparison.Ordinal)
                && template.Contains("one retry queued", StringComparison.Ordinal))
            {
                // Rule 7 early-chip retry: the row goes back in the queue and starts again.
                Interlocked.Increment(ref _mpTotal);
                Note($"retry queued {Value("Chip")}");
            }
            else if (template is not null && template.StartsWith("[StreamResolver] Starting resolve", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Value("Kind") as string == "MP" ? ref _mpStarted : ref _fbStarted);
            }
            else if (template is not null && template.StartsWith("[StreamResolver] LocalService listed", StringComparison.Ordinal))
            {
                // Extra = chip rows actually added to the queue (duplicates are skipped).
                if (Value("Extra") is int added && added > 0)
                {
                    Interlocked.Add(ref _mpTotal, added);
                    lock (_tl)
                        _tl.ChipsQueued.Add(new ChipsQueued(RowIdOf(Value("Url") as string ?? ""), T, added));
                }
            }
            else if (template is not null && template.StartsWith("[StreamResolver] MP page", StringComparison.Ordinal))
            {
                // A seed that listed chips: it goes back in the queue as its first chip
                // (one MP resolve ended without a yield) with the others beside it.
                if (Value("Extra") is int rows)
                {
                    Interlocked.Add(ref _mpTotal, rows - 1);
                    Interlocked.Decrement(ref _mpStarted);
                    lock (_tl)
                        _tl.ChipsQueued.Add(new ChipsQueued(RowIdOf(Value("Url") as string ?? ""), T, rows));
                }
            }

            if (level >= LogLevel.Information)
            {
                lock (_tl)
                    _tl.Log.Add((T, message));
            }
        }

        private void LocalServiceEvent(string rowId, string what)
        {
            lock (_tl)
                _tl.LocalServiceEvents.Add((T, rowId, what));
            Note($"LocalService {rowId}: {what}");
        }

        // ---- scripted LocalService (HTTP) ----

        private sealed class ScriptedLocalService(Run run) : HttpMessageHandler
        {
            private const string Health = """{"capabilities":["play.stream","mp.chrome"],"version":"9.9.9"}""";
            private static readonly Regex ChipUrl = new(@"\{chipUrl:(.*?)\}", RegexOptions.Compiled);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                run.Bump();
                var path = request.RequestUri?.AbsolutePath ?? "";
                if (path.EndsWith("/health", StringComparison.Ordinal))
                    return Json(HttpStatusCode.OK, Health);
                if (path.EndsWith("/mp", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
                    return await MpAsync(request, ct).ConfigureAwait(false);
                if (path.Contains("/play/", StringComparison.Ordinal))
                    return await PlayAsync(request, ct).ConfigureAwait(false);
                return Json(HttpStatusCode.NotFound, "{}");
            }

            private async Task<HttpResponseMessage> PlayAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var raw = request.RequestUri!.OriginalString;
                var rest = raw[(raw.IndexOf("/play/", StringComparison.Ordinal) + "/play/".Length)..];
                var q = rest.IndexOf('?');
                var page = Uri.UnescapeDataString(q < 0 ? rest : rest[..q]);
                var row = run.RowFor(page);
                int call;
                lock (run._playCalls)
                    call = run._playCalls[row.Id] = run._playCalls.GetValueOrDefault(row.Id) + 1;
                var script = (call > 1 ? row.Retry : null) ?? row.Play ?? throw new InvalidOperationException($"{row.Id} is not an FB row");
                Interlocked.Increment(ref run._plays);
                run.LocalServiceEvent(row.Id, "GET /play");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(script.AnswerAt), run._clock, ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref run._plays);
                    run.Bump();
                }

                run.LocalServiceEvent(row.Id, $"/play answers {script.Answer}");
                var headers = script.Link switch
                {
                    PlayLink.Soak => $$""","requestHeaders":{"Referer":"{{FbPage(row.Id)}}"}""",
                    PlayLink.SingleUse => $$""","requestHeaders":{"Referer":"{{FbPage(row.Id)}}","Cookie":"session={{row.Id}}"}""",
                    _ => ""
                };
                return script.Answer switch
                {
                    PlayAnswer.Playlist => Json(HttpStatusCode.OK, $$"""{"url":"{{FbPlaylist(row.Id)}}" """.TrimEnd() + headers + "}"),
                    PlayAnswer.CdnUnreachable => Json(HttpStatusCode.GatewayTimeout, """{"error":"page navigation timed out"}"""),
                    _ => Json(HttpStatusCode.NotFound, """{"error":"M3U8 not captured"}""")
                };
            }

            private async Task<HttpResponseMessage> MpAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var page = doc.RootElement.GetProperty("pageUrl").GetString() ?? "";
                var label = doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                var row = run.RowFor(page);
                var script = row.Mp ?? throw new InvalidOperationException($"{row.Id} is not an MP row");
                var feed = new LineFeedStream();
                var startedAt = run.T;
                Interlocked.Increment(ref run._mpOpen);
                run.LocalServiceEvent(row.Id, label is null ? "POST /mp" : $"POST /mp (stream={label})");
                _ = Task.Run(async () =>
                {
                    // The client hanging up (cancel before headers, or disposing the body
                    // mid-harvest) closes the connection, and LocalService stops this /mp.
                    using var hangUp = CancellationTokenSource.CreateLinkedTokenSource(ct, feed.Aborted);
                    try
                    {
                        foreach (var line in script.Lines.OrderBy(l => l.At))
                        {
                            var wait = startedAt + line.At - run.T;
                            if (wait > 0)
                                await Task.Delay(TimeSpan.FromSeconds(wait), run._clock, hangUp.Token).ConfigureAwait(false);
                            if (hangUp.IsCancellationRequested)
                                break;
                            var json = ChipUrl.Replace(line.Json, m => ChipPlaylist(row.Id, m.Groups[1].Value));
                            feed.Write(json + "\n");
                            run.LocalServiceEvent(row.Id, "/mp " + line.What);
                            if (line.What == "done" || line.What.StartsWith("error", StringComparison.Ordinal))
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    finally
                    {
                        feed.Complete();
                        if (hangUp.IsCancellationRequested)
                            run.LocalServiceEvent(row.Id, "/mp closed by the client");
                        Interlocked.Decrement(ref run._mpOpen);
                        run.Bump();
                    }
                }, CancellationToken.None);

                await Task.Delay(TimeSpan.FromSeconds(script.HeadersAt), run._clock, ct).ConfigureAwait(false);
                run.LocalServiceEvent(row.Id, "/mp 200 headers");
                var content = new StreamContent(feed);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(MpNdjson.ContentType);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
                new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        /// <summary>An NDJSON body that LocalService writes line by line as the clock reaches each milestone.</summary>
        private sealed class LineFeedStream : System.IO.Stream
        {
            private readonly Channel<byte[]> _lines = Channel.CreateUnbounded<byte[]>();
            private readonly CancellationTokenSource _aborted = new();
            private byte[]? _current;
            private int _pos;

            /// <summary>Cancelled when the client disposes the body (the connection closes).</summary>
            public CancellationToken Aborted => _aborted.Token;

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_aborted.IsCancellationRequested)
                    _aborted.Cancel();
                base.Dispose(disposing);
            }

            public void Write(string text) => _lines.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

            public void Complete() => _lines.Writer.TryComplete();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                while (_current is null || _pos >= _current.Length)
                {
                    if (!await _lines.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                        return 0;
                    if (_lines.Reader.TryRead(out var next))
                    {
                        _current = next;
                        _pos = 0;
                    }
                }

                var n = Math.Min(buffer.Length, _current.Length - _pos);
                _current.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // ---- scripted CDN health check ----

        private sealed class ScriptedHealth(Run run) : IStreamHealthChecker
        {
            public Task<StreamHealth> CheckStreamHealthAsync(string m3u8Url, string refererUrl, CancellationToken cancellationToken = default) =>
                CheckAsync(m3u8Url, cancellationToken);

            public Task<StreamHealth> CheckStreamHealthAsync(string m3u8Url, string refererUrl, StreamHealthProbe? probe, CancellationToken cancellationToken = default) =>
                CheckAsync(m3u8Url, cancellationToken);

            private async Task<StreamHealth> CheckAsync(string url, CancellationToken ct)
            {
                var (key, takes, status) = Verdict(url);
                Interlocked.Increment(ref run._healthOpen);
                run.Note($"health start {key}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(takes), run._clock, ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref run._healthOpen);
                }

                lock (run._tl)
                    run._tl.HealthVerdicts.Add((run.T, key, status));
                run.Note($"health {key}: {status}");
                return new StreamHealth { Status = status, Url = url };
            }

            private (string Key, double Takes, StreamHealthStatus Status) Verdict(string url)
            {
                var path = new Uri(url).AbsolutePath.Trim('/');
                var parts = path.Split('/');
                if (parts.Length == 1)
                {
                    var id = parts[0][..^".m3u8".Length];
                    var play = run._rowsById[id].Play!;
                    return (id, play.HealthTakes, play.Health);
                }

                var rowId = parts[0];
                var label = Uri.UnescapeDataString(parts[1][..^".m3u8".Length]);
                var mp = run._rowsById[rowId].Mp!;
                var chip = mp.ChipVerdicts[label];
                var key = $"{rowId}:{label}";
                // A second check of the same chip (the rule 7 early-chip retry) may answer differently.
                var checks = run._chipChecks.AddOrUpdate(key, 1, (_, n) => n + 1);
                var status = checks > 1 && mp.RetryVerdicts.TryGetValue(label, out var retry) ? retry : chip.Health;
                return (key, chip.HealthTakes, status);
            }
        }

        // ---- LocalLanPlayService wrapper that records each resolver call ----

        private sealed class RecordingLan(Run run, LocalLanPlayService inner) : ILocalLanPlayService, IMpHarvestProbe
        {
            private ILocalLanPlayService Inner => inner;

            public bool IsMpChipReady(string pageUrl, string? chip) => inner.IsMpChipReady(pageUrl, chip);

            public bool RetryEarlyMpChip(string pageUrl, string? chip) => inner.RetryEarlyMpChip(pageUrl, chip);

            public string? MpChipHandover(string pageUrl, string? chip) => inner.MpChipHandover(pageUrl, chip);

            public bool CanStartMp(string pageUrl, string? chip) => inner.CanStartMp(pageUrl, chip);

            public IDisposable WantMpPage(string pageUrl, CancellationToken runToken) => inner.WantMpPage(pageUrl, runToken);

            public Task WaitForMpHarvestChangeAsync(CancellationToken cancellationToken) => inner.WaitForMpHarvestChangeAsync(cancellationToken);

            public Task<M3U8Response?> ResolveM3U8UrlAsync(string streamUrl, CancellationToken cancellationToken = default) =>
                ResolveM3U8UrlAsync(streamUrl, null, null, null, cancellationToken);

            public Task<M3U8Response?> ResolveM3U8UrlAsync(string streamUrl, string? playerStreamName, CancellationToken cancellationToken = default) =>
                ResolveM3U8UrlAsync(streamUrl, playerStreamName, null, null, cancellationToken);

            public async Task<M3U8Response?> ResolveM3U8UrlAsync(
                string streamUrl,
                string? playerStreamName,
                string? resolutionStrategy,
                string? source,
                CancellationToken cancellationToken = default)
            {
                var call = run.BeginCall(streamUrl, playerStreamName);
                try
                {
                    var result = await Inner.ResolveM3U8UrlAsync(streamUrl, playerStreamName, resolutionStrategy, source, cancellationToken)
                        .ConfigureAwait(false);
                    run.EndCall(call, string.IsNullOrWhiteSpace(result?.Url)
                        ? $"no playlist (chips listed: {result?.Streams?.Count ?? 0})"
                        : $"playlist {result!.SelectedStream}".TrimEnd());
                    return result;
                }
                catch (Exception ex)
                {
                    run.EndCall(call, $"{ex.GetType().Name}: {ex.Message}");
                    throw;
                }
            }

            public Task<bool> SupportsPlayStreamQueryAsync(CancellationToken cancellationToken = default) => Inner.SupportsPlayStreamQueryAsync(cancellationToken);
            public Task<bool> SupportsComputeHostAsync(CancellationToken cancellationToken = default) => Inner.SupportsComputeHostAsync(cancellationToken);
            public string? DiscoveredServiceVersion => Inner.DiscoveredServiceVersion;
            public Task<string?> GetDiscoveredServiceVersionAsync(CancellationToken cancellationToken = default) => Inner.GetDiscoveredServiceVersionAsync(cancellationToken);
            public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Inner.IsAvailableAsync(cancellationToken);
            public bool UsesRemoteCompute => Inner.UsesRemoteCompute;
            public string? LastRemoteComputeError => Inner.LastRemoteComputeError;
            public Task<string?> GetServiceBaseUrlAsync(CancellationToken cancellationToken = default) => Inner.GetServiceBaseUrlAsync(cancellationToken);
            public HttpStatusCode? LastHealthStatus => Inner.LastHealthStatus;
        }

        private sealed class RecordingLogger<T>(Run run) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                run.OnLog(logLevel, state as IReadOnlyList<KeyValuePair<string, object?>>, formatter(state, exception));
        }
    }
}
