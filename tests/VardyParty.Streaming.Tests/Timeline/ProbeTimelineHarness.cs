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
/// (window, MP single-flight, sibling queueing), LocalLanPlayService (throttle slots,
/// <c>/mp</c> NDJSON harvest, MpHarvestSession seed rules), ResolverRaceThrottle,
/// StreamTestOrderPolicy (try-order), FirstPlayHoldPolicy (first-play hold) and
/// StreamSwitchingService (channel numbers). Only LocalService (HTTP) and the CDN
/// health check are scripted, on a <see cref="MilestoneClock"/>: the run jumps from
/// one milestone to the next, so a 111s harvest runs in milliseconds.
/// Not modelled: the orchestrator's deferred re-queue passes, the prewarmer and its
/// early release of the priority window, and our own real-time HTTP timeouts
/// (CancelAfter in LocalLanPlayService is not on a TimeProvider).
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
        private static readonly TimeSpan MaxSettle = TimeSpan.FromSeconds(2);

        private readonly ProbeScenario _scenario;
        private readonly MilestoneClock _clock = new(Start);
        private readonly ProbeTimeline _tl;
        private readonly ResolverRaceThrottle _throttle = new();
        private readonly StreamSwitchingService _pool = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<string, RowSpec> _rowsById = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<StreamModel, string> _keyByRef = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<StreamModel, int> _catalogIndex = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<int> _resolved = new();
        private readonly SortedDictionary<int, EnrichedStream> _held = new();
        private readonly object _gate = new();
        private List<StreamModel> _ordered = new();
        private bool[] _mpCatalog = [];
        private int _nextExtra;
        private bool _hasPlayed;
        private IDisposable? _priorityHold;
        private ITimer? _priorityTimer;
        private long _activity;
        private int _idleWorkers = 1;
        private int _started;
        private int _knownTotal;
        private int _plays;
        private int _mpOpen;
        private int _healthOpen;

        public Run(ProbeScenario scenario)
        {
            _scenario = scenario;
            _tl = new ProbeTimeline { Scenario = scenario };
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
            _idleWorkers = Enumerable.Range(0, 50).Select(_ =>
            {
                Thread.Sleep(0);
                return BusyWorkers();
            }).Min();

            var catalog = _scenario.Rows.Select(BuildStream).ToList();
            for (var i = 0; i < catalog.Count; i++)
                _keyByRef[catalog[i]] = _scenario.Rows[i].Id;

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
            for (var i = 0; i < _ordered.Count; i++)
                _catalogIndex[_ordered[i]] = i;
            _mpCatalog = _ordered.Select(StreamRecommendationPolicy.IsMpLike).ToArray();
            _nextExtra = _ordered.Count;
            _knownTotal = _ordered.Count;
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
            lan.EndpointsForTests.Remember(Endpoint, DateTimeOffset.UtcNow);

            var resolver = new StreamResolver(
                new ScriptedHealth(this),
                new RecordingLan(this, lan),
                new V2PlaybackTransportPlugin(),
                new RecordingLogger<StreamResolver>(this),
                _throttle)
            {
                DelayAsync = (d, ct) => Task.Delay(d, _clock, ct)
            };

            var consume = Task.Run(async () =>
            {
                await foreach (var row in resolver
                                   .ResolveStreamsIncrementallyAsync(_ordered, 2, _cts.Token)
                                   .ConfigureAwait(false))
                {
                    Accept(row);
                }
            });

            var horizonHit = false;
            while (true)
            {
                Settle();
                TakeSnapshot();
                if (consume.IsCompleted)
                    break;

                var next = _clock.NextDue;
                if (next is null)
                {
                    Settle(TimeSpan.FromMilliseconds(50));
                    if (consume.IsCompleted)
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
            }
            catch (OperationCanceledException) when (horizonHit)
            {
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Resolver faulted: {ex.Message}\n{_tl.Render()}", ex);
            }

            _priorityTimer?.Dispose();
            _priorityHold?.Dispose();
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
        /// </summary>
        private void Settle(TimeSpan? quiet = null)
        {
            var window = quiet ?? Quiet;
            var sw = Stopwatch.StartNew();
            var last = Interlocked.Read(ref _activity);
            var quietFrom = sw.Elapsed;
            while (sw.Elapsed < MaxSettle)
            {
                Thread.Sleep(0);
                var now = Interlocked.Read(ref _activity);
                if (now != last || ThreadPool.PendingWorkItemCount > 0 || BusyWorkers() > _idleWorkers)
                {
                    last = now;
                    quietFrom = sw.Elapsed;
                    continue;
                }

                if (sw.Elapsed - quietFrom >= window)
                    return;
            }
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
                    Math.Max(0, Volatile.Read(ref _knownTotal) - Volatile.Read(ref _started)),
                    _throttle.RaceInFlight,
                    inFlight);
                if (_tl.Snapshots.Count > 0 && Math.Abs(_tl.Snapshots[^1].At - snap.At) < 1e-9)
                    _tl.Snapshots[^1] = snap;
                else
                    _tl.Snapshots.Add(snap);
            }
        }

        // ---- the orchestrator's Accept/Flush, using the production first-play hold ----

        private void Accept(EnrichedStream row)
        {
            lock (_gate)
            {
                var key = KeyOf(row.Stream);
                lock (_tl)
                    _tl.Results.Add(new RowResult(key, T, row.Status, row.ErrorMessage));
                Note($"yield {key}: {row.Status}{(row.ErrorMessage is { } e ? $" ({e})" : "")}");

                var index = _catalogIndex.TryGetValue(row.Stream, out var known) ? known : _nextExtra++;
                _resolved.Add(index);
                if (row.Status == StreamResolutionStatus.Healthy)
                    _held[index] = row;
                Flush();
            }
        }

        private void Flush()
        {
            var frontier = FirstPlayHoldPolicy.Frontier(_mpCatalog, _resolved);
            var ready = _held
                .Where(p => FirstPlayHoldPolicy.IsReleasable(p.Key, frontier, _ordered.Count, _hasPlayed))
                .Select(p => p.Key)
                .ToList();
            foreach (var index in ready)
            {
                var row = _held[index];
                _held.Remove(index);
                _pool.AddHealthyStream(row);
                var key = KeyOf(row.Stream);
                Note($"pool +{key} (#{_pool.GetHealthyStreams().Count})");
                if (_hasPlayed)
                    continue;

                _hasPlayed = true;
                _tl.FirstPlay = (T, key);
                Note($"FIRST PLAY on {key}");
                if (_scenario.PriorityWindowAfterFirstPlay)
                {
                    _priorityHold = _throttle.BeginPlaybackPriority();
                    _tl.PriorityWindow = (T, T + ResolverRaceThrottle.PlaybackPriorityTimeout.TotalSeconds);
                    Note($"priority window starts ({ResolverRaceThrottle.PlaybackPriorityTimeout.TotalSeconds}s)");
                    _priorityTimer = _clock.CreateTimer(_ =>
                    {
                        _priorityHold?.Dispose();
                        Note("priority window ends");
                    }, null, ResolverRaceThrottle.PlaybackPriorityTimeout, Timeout.InfiniteTimeSpan);
                }

                if (_scenario.StartupRaceDrain)
                {
                    _ = Task.Run(async () =>
                    {
                        using var race = _throttle.BeginRace();
                        Note("startup race: DrainOthersAsync");
                        await _throttle.DrainOthersAsync(_cts.Token).ConfigureAwait(false);
                        _tl.DrainedAt = T;
                        Note("startup race: drained");
                    });
                }
            }
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
            if (template is not null && template.StartsWith("[StreamResolver] Starting resolve", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _started);
                if (Value("Count") is int count)
                    Volatile.Write(ref _knownTotal, count);
            }
            else if (template is not null && template.StartsWith("[StreamResolver] LocalService listed", StringComparison.Ordinal))
            {
                // "listed N more" repeats on every chip's yield (duplicates are skipped
                // by the resolver); count only rows actually added to the queue.
                var url = Value("Url") as string ?? "";
                if (Value("Total") is int total)
                {
                    var added = total - Interlocked.Exchange(ref _knownTotal, total);
                    if (added > 0)
                    {
                        lock (_tl)
                            _tl.ChipsQueued.Add(new ChipsQueued(RowIdOf(url), T, added));
                    }
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
                var script = row.Play ?? throw new InvalidOperationException($"{row.Id} is not an FB row");
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
                return script.Answer switch
                {
                    PlayAnswer.Playlist => Json(HttpStatusCode.OK, $$"""{"url":"{{FbPlaylist(row.Id)}}"}"""),
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
                    try
                    {
                        foreach (var line in script.Lines.OrderBy(l => l.At))
                        {
                            var wait = startedAt + line.At - run.T;
                            if (wait > 0)
                                await Task.Delay(TimeSpan.FromSeconds(wait), run._clock, ct).ConfigureAwait(false);
                            var json = ChipUrl.Replace(line.Json, m => ChipPlaylist(row.Id, m.Groups[1].Value));
                            feed.Write(json + "\n");
                            run.LocalServiceEvent(row.Id, "/mp " + line.What);
                            if (line.What == "done")
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    finally
                    {
                        feed.Complete();
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
            private byte[]? _current;
            private int _pos;

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
                var chip = run._rowsById[rowId].Mp!.ChipVerdicts[label];
                return ($"{rowId}:{label}", chip.HealthTakes, chip.Health);
            }
        }

        // ---- LocalLanPlayService wrapper that records each resolver call ----

        private sealed class RecordingLan(Run run, LocalLanPlayService inner) : ILocalLanPlayService
        {
            private ILocalLanPlayService Inner => inner;

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
