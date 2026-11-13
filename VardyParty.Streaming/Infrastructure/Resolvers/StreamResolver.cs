using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using Stream = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

public class StreamResolver(
    IStreamHealthChecker healthChecker,
    ILocalLanPlayService localLanPlayService,
    IDiscoveredChipNormalizer chipNormalizer,
    ILogger<StreamResolver> logger,
    ResolverRaceThrottle? raceThrottle = null) : IStreamResolver
{
    /// <summary>
    /// Waits before re-asking an unavailable local service about the same row.
    /// After these the row is <see cref="StreamResolutionStatus.Deferred"/>, never Failed.
    /// </summary>
    internal static readonly TimeSpan[] InfrastructureRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    /// <summary>Test seam for the infrastructure retry waits.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    public async IAsyncEnumerable<EnrichedStream> ResolveStreamsIncrementallyAsync(
        List<Stream> streams,
        int batchSize = 3,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Action<int>? onTotalStreamsKnown = null)
    {
        if (streams == null || streams.Count == 0)
        {
            logger.LogInformation("[StreamResolver] No streams to resolve");
            yield break;
        }

        var pending = streams.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in pending)
        {
            seen.Add(CandidateKey(stream));
        }

        // Catalog MP rows often have no chip labels yet — LocalService discovers them
        // on the first /mp. Keep overlay total unknown (indeterminate) until then.
        if (!pending.Any(HasUnknownMpChipFanOut))
        {
            onTotalStreamsKnown?.Invoke(pending.Count);
        }

        var concurrency = Math.Max(1, batchSize);
        logger.LogInformation(
            "[StreamResolver] Starting incremental resolution of {Count} streams (concurrency {Concurrency}; yield-as-ready; MP single-flight)",
            pending.Count, concurrency);

        // Yield each finished resolve immediately. FB rows run up to `concurrency`
        // at once. MP rows have their own lane of one (docs/PROBE_SCHEDULING_RULES.md
        // rules 3-4): LocalService is single-threaded for POST /mp, and an in-flight
        // /mp never takes an FB place. A seed that only lists chips is re-queued as
        // its first chip with the rest beside it (rules 5-6). First play is first
        // come, first served (StreamResolutionOrchestrator, rule 9). Chip siblings
        // from /mp are inserted into `pending` and picked up by FillWindow. With a
        // High or Medium recommendations (rule 1) the other rows wait for the head
        // start (TopTierHeadStart).
        var gate = new SemaphoreSlim(concurrency, concurrency);
        var inFlight = new List<(Task<ResolveOutcome> Task, bool IsMp)>();
        var started = new HashSet<int>();
        var nextIndex = 0;
        var activeMp = 0;
        var activeFb = 0;
        var pauseLogged = false;
        var harvestProbe = localLanPlayService as IMpHarvestProbe;
        // MP pages this run has touched: while the run lives it wants their harvest; a
        // cancelled run lets go, so LocalService's single /mp is not held for nobody.
        var wantedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageWants = new List<IDisposable>();
        // Rule 1: the head start running when this run began (the first pass only).
        var headStart = raceThrottle?.CurrentHeadStart;
        var yielded = new HashSet<Stream>(ReferenceEqualityComparer.Instance);

        // Rule 1 (TopTierHeadStart). High rows are never held. A Medium row waits during
        // the head start while a High row has no verdict and there are two or more High
        // rows; after it, a Medium MP row still waits while a High FB row is queued. A
        // finding row waits during the head start while a High or Medium row has no
        // verdict; after it, an MP finding row still waits while a High or Medium FB row
        // is queued. FB rows need no extra check after the head start: the FB window
        // takes rows in try-order, which puts High, then Medium, first.
        bool HoldsFinding(int index)
        {
            var candidate = pending[index];
            if (headStart is null || headStart.IsTopTier(candidate))
                return false;
            var highWaiting = false;
            var tierWaiting = false;
            var highFbQueued = false;
            var tierFbQueued = false;
            for (var i = 0; i < pending.Count; i++)
            {
                var row = pending[i];
                var high = headStart.IsTopTier(row);
                if ((!high && !headStart.IsMedium(row)) || yielded.Contains(row))
                    continue;
                tierWaiting = true;
                highWaiting |= high;
                if (!started.Contains(i) && !MpPageUrl.IsV2Stream(row))
                {
                    tierFbQueued = true;
                    highFbQueued |= high;
                }
            }

            var isMp = MpPageUrl.IsV2Stream(candidate);
            if (headStart.IsMedium(candidate))
            {
                if (!headStart.IsElapsed)
                    return highWaiting && headStart.Count >= 2;
                return isMp && highFbQueued;
            }

            if (!tierWaiting)
                return false;
            if (!headStart.IsElapsed)
                return true;
            return isMp && tierFbQueued;
        }

        // Finding rows are queued and held by the head start; wake when it elapses.
        bool WaitingOnHeadStart()
        {
            if (headStart is null)
                return false;
            var held = false;
            for (var i = 0; i < pending.Count && !held; i++)
            {
                if (!started.Contains(i) && HoldsFinding(i))
                    held = true;
            }

            headStart.FindingHeld = held;
            return held && !headStart.IsElapsed;
        }

        async Task<ResolveOutcome> StartFbAsync(Stream stream)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ResolveAndTestStreamAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        bool HasUnstarted()
        {
            for (var i = 0; i < pending.Count; i++)
            {
                if (!started.Contains(i))
                    return true;
            }

            return false;
        }

        void Start(int index, bool isMp)
        {
            started.Add(index);
            nextIndex = FirstUnstarted(pending, started);

            var stream = pending[index];
            if (isMp)
            {
                activeMp++;
                if (harvestProbe is not null && wantedPages.Add(stream.Url))
                    pageWants.Add(harvestProbe.WantMpPage(stream.Url, cancellationToken));
            }
            else
            {
                activeFb++;
            }

            logger.LogInformation(
                "[StreamResolver] Starting resolve {Index}/{Count} for {Channel} ({Kind})",
                index + 1, pending.Count, stream.Channel, isMp ? "MP" : "FB");
            inFlight.Add((isMp ? ResolveAndTestStreamAsync(stream, cancellationToken) : StartFbAsync(stream), isMp));
        }

        void FillWindow()
        {
            // A startup race or the post-first-play priority window holds the
            // LocalService cap: finish what is in flight, start no new browser
            // probe. MP chips the harvest already holds need nothing from
            // LocalService, so they may still be probed (rule 11).
            var raceInFlight = raceThrottle?.RaceInFlight == true;

            if (activeMp == 0 && NextMp(raceInFlight) is int mpIndex)
                Start(mpIndex, isMp: true);

            if (raceInFlight)
                return;

            while (activeFb < concurrency)
            {
                int? pick = null;
                for (var i = nextIndex; i < pending.Count; i++)
                {
                    if (!started.Contains(i) && !MpPageUrl.IsV2Stream(pending[i]) && !HoldsFinding(i))
                    {
                        pick = i;
                        break;
                    }
                }

                if (pick is not int index)
                    break;

                Start(index, isMp: false);
            }
        }

        // The MP lane takes a chip the harvest already holds first, so one chip
        // still soaking (up to MpHarvestSession.ProbeReadyWait) does not hold up
        // the ready ones; otherwise the next MP row in queue order that will not just
        // wait on a running harvest (rule 7: a chip the harvest has not answered yet,
        // or another page's row, stays queued and is woken by the harvest). During a
        // race only ready chips may start. A finding row held by the top-tier head
        // start (rule 1) does not start at all.
        int? NextMp(bool readyOnly)
        {
            int? firstMp = null;
            for (var i = nextIndex; i < pending.Count; i++)
            {
                if (started.Contains(i) || !MpPageUrl.IsV2Stream(pending[i]))
                    continue;
                if (HoldsFinding(i))
                    continue;
                var chip = GetPlayerStreamName(pending[i]);
                if (harvestProbe?.IsMpChipReady(pending[i].Url, chip) == true)
                    return i;
                if (firstMp is null && (harvestProbe is null || harvestProbe.CanStartMp(pending[i].Url, chip)))
                    firstMp = i;
            }

            return readyOnly ? null : firstMp;
        }

        // The MP lane is empty but MP rows are queued: they wait on the running harvest.
        bool MpWaitingOnHarvest()
        {
            if (harvestProbe is null || activeMp > 0)
                return false;
            for (var i = 0; i < pending.Count; i++)
            {
                if (!started.Contains(i) && MpPageUrl.IsV2Stream(pending[i]))
                    return true;
            }

            return false;
        }

        async Task WakeAsync(bool onRace, bool onHarvest, bool onHeadStart, CancellationToken token)
        {
            // Race end, or a harvest change that may have made a chip ready. Only what
            // the loop is actually waiting on: a race wait with no race in flight
            // completes at once and would spin this loop.
            try
            {
                var waits = new List<Task>();
                if (onRace && raceThrottle is not null)
                    waits.Add(raceThrottle.WaitUntilRaceEndsAsync(token));
                if (onHarvest && harvestProbe is not null)
                    waits.Add(harvestProbe.WaitForMpHarvestChangeAsync(token));
                if (onHeadStart && headStart is not null)
                    waits.Add(headStart.Elapsed.WaitAsync(token));
                if (waits.Count > 0)
                    await Task.WhenAny(waits).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            FillWindow();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var waitingOnRace = raceThrottle?.RaceInFlight == true && HasUnstarted();
                if (!waitingOnRace)
                    pauseLogged = false;
                var waitingOnHarvest = MpWaitingOnHarvest();
                var waitingOnHeadStart = WaitingOnHeadStart();

                if (inFlight.Count == 0 && !waitingOnRace && !waitingOnHarvest && !waitingOnHeadStart)
                    break;

                Task<ResolveOutcome>? completedTask;
                if (waitingOnRace || waitingOnHarvest || waitingOnHeadStart)
                {
                    if (waitingOnRace && inFlight.Count == 0 && !pauseLogged)
                    {
                        pauseLogged = true;
                        logger.LogInformation(
                            "[StreamResolver] Pausing candidate testing while a playback race holds the local-service cap");
                    }

                    using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    // Arm the wake first, then look again: a chip that turned ready (or a
                    // race that ended) after the last FillWindow cannot be missed.
                    var wake = WakeAsync(waitingOnRace, waitingOnHarvest, waitingOnHeadStart, wakeCts.Token);
                    FillWindow();
                    var first = await Task.WhenAny(inFlight.Select(x => (Task)x.Task).Append(wake)).ConfigureAwait(false);
                    wakeCts.Cancel();
                    await wake.ConfigureAwait(false);
                    if (ReferenceEquals(first, wake))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FillWindow();
                        continue;
                    }

                    completedTask = (Task<ResolveOutcome>)first;
                }
                else
                {
                    completedTask = await Task.WhenAny(inFlight.Select(x => x.Task)).ConfigureAwait(false);
                }

                var index = inFlight.FindIndex(x => ReferenceEquals(x.Task, completedTask));
                var entry = inFlight[index];
                inFlight.RemoveAt(index);
                if (entry.IsMp)
                    activeMp--;
                else
                    activeFb--;

                var outcome = await entry.Task.ConfigureAwait(false);

                if (outcome.RequeueAsChip)
                {
                    // Rules 5-6: the seed only listed chips. It goes back in the queue as
                    // its first chip (same object, so the orchestrator keeps its catalog
                    // place) with every other chip beside it; the harvest carries on.
                    var seed = outcome.Enriched.Stream;
                    var seedIndex = pending.FindIndex(s => ReferenceEquals(s, seed));
                    if (seedIndex >= 0)
                    {
                        pending.RemoveAt(seedIndex);
                        started.Remove(seedIndex);
                        ShiftStarted(started, seedIndex + 1, -1);
                    }

                    seen.Add(CandidateKey(seed));
                    var rows = new List<Stream> { seed };
                    rows.AddRange(outcome.Siblings.Where(s => seen.Add(CandidateKey(s))));
                    InsertUnstarted(pending, started, rows);
                    nextIndex = FirstUnstarted(pending, started);
                    onTotalStreamsKnown?.Invoke(pending.Count);
                    logger.LogInformation(
                        "[StreamResolver] MP page {Url} listed {Extra} chips ({Chips}); queued as rows; total now {Total}",
                        seed.Url,
                        rows.Count,
                        string.Join(", ", rows.Select(s => s.PlayerStream)),
                        pending.Count);

                    cancellationToken.ThrowIfCancellationRequested();
                    FillWindow();
                    continue;
                }

                seen.Add(CandidateKey(outcome.Enriched.Stream));

                var added = outcome.Siblings.Count > 0
                    ? EnqueueMpSiblingsNext(pending, started, ref nextIndex, seen, outcome.Siblings)
                    : [];

                // Publish total once MP chip inventory is known (siblings and/or sole chip).
                if (entry.IsMp || outcome.Siblings.Count > 0)
                {
                    onTotalStreamsKnown?.Invoke(pending.Count);
                }

                if (added.Count > 0)
                {
                    logger.LogInformation(
                        "[StreamResolver] LocalService listed {Extra} more MP chips ({Chips}) for {Url}; total now {Total}",
                        added.Count,
                        string.Join(", ", added.Select(s => s.PlayerStream)),
                        outcome.Enriched.Stream.Url,
                        pending.Count);
                }

                logger.LogInformation("[StreamResolver] Yielding resolved stream: {Channel} ({Status})",
                    outcome.Enriched.Stream.Channel, outcome.Enriched.Status);
                yielded.Add(outcome.Enriched.Stream);
                yield return outcome.Enriched;

                cancellationToken.ThrowIfCancellationRequested();
                FillWindow();
            }
        }
        finally
        {
            foreach (var want in pageWants)
                want.Dispose();
            if (headStart is not null)
                headStart.FindingHeld = false;
        }

        logger.LogInformation("[StreamResolver] Completed incremental resolution of all streams");
    }

    public async Task<string?> ResolveM3U8UrlAsync(
        Stream stream,
        string refererUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation("[StreamResolver] Resolving m3u8 URL for single stream: {Channel}", stream.Channel);

            var m3u8Url = await GetM3U8UrlInternalAsync(stream, cancellationToken);

            if (!string.IsNullOrEmpty(m3u8Url))
            {
                logger.LogInformation("[StreamResolver] Successfully resolved m3u8 URL for {Channel}", stream.Channel);
                return m3u8Url;
            }

            logger.LogWarning("[StreamResolver] No m3u8 URL returned for {Channel}", stream.Channel);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StreamResolver] Failed to resolve m3u8 URL for {Channel} with {url}", stream.Channel,
                stream.Url);
            return null;
        }
    }

    private readonly record struct ResolveOutcome(EnrichedStream Enriched, IReadOnlyList<Stream> Siblings, bool RequeueAsChip = false);

    private static string CandidateKey(Stream stream) =>
        $"{stream.Url}\n{stream.PlayerStream?.Trim() ?? ""}";

    private Task<ResolveOutcome> ResolveAndTestStreamAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        return ResolveAndTestStreamInternalAsync(stream, cancellationToken);
    }

    private async Task<ResolveOutcome> ResolveAndTestStreamInternalAsync(Stream stream,
        CancellationToken cancellationToken)
    {
        var enriched = new EnrichedStream { Stream = stream, Status = StreamResolutionStatus.Pending };

        if (StreamCandidateRules.ShouldSkipCountdown(stream.IsCountdown))
        {
            enriched.Status = StreamResolutionStatus.Failed;
            enriched.ErrorMessage = "Stream page is in countdown (not started yet)";
            logger.LogInformation("[StreamResolver] Skipping {Channel}: countdown active on stream page", stream.Channel);
            return new ResolveOutcome(enriched, []);
        }

        try
        {
            // Step 1: Resolve m3u8 URL
            logger.LogInformation("[StreamResolver] Resolving m3u8 for {Channel}", stream.Channel);
            M3U8Response? m3u8Response;
            try
            {
                m3u8Response = await ResolveWithInfrastructureRetryAsync(stream, cancellationToken);
            }
            catch (LocalServiceUnavailableException ex) when (ex.Reason == LocalServiceUnavailableReason.CdnUnreachable)
            {
                // The service ran but could not reach the CDN. Not dead: the
                // orchestrator rechecks it after a back-off (2 min, 5 min, 10 min).
                enriched.Status = StreamResolutionStatus.Deferred;
                enriched.DeferReason = StreamDeferReason.CdnUnreachable;
                enriched.ErrorMessage = ex.Message;
                logger.LogWarning("[StreamResolver] Deferring {Channel}: {Error}", stream.Channel, enriched.ErrorMessage);
                return new ResolveOutcome(enriched, []);
            }
            catch (LocalServiceUnavailableException ex)
            {
                // The service didn't answer, so we know nothing about this
                // stream. Never Failed, never reported; the orchestrator re-queues it.
                enriched.Status = StreamResolutionStatus.Deferred;
                enriched.DeferReason = StreamDeferReason.LocalService;
                enriched.ErrorMessage = $"Local service unavailable ({ex.Reason}): {ex.Message}";
                logger.LogWarning("[StreamResolver] Deferring {Channel}: {Error}", stream.Channel, enriched.ErrorMessage);
                return new ResolveOutcome(enriched, []);
            }

            var m3u8Url = m3u8Response?.Url;

            if (ChipListSeed(stream, m3u8Response) is { } chips)
            {
                // Rules 5-6: an unlabeled MP seed answered with the page's chips
                // (the chip list arrived, a first chip arrived, or the 30s seed
                // deadline passed). Not a verdict: this row becomes its first chip and
                // every listed chip is queued now, not after this row's health check.
                // The re-queued row reads its chip from the running harvest.
                V2StreamExpander.ApplySelectedChip(stream, chips.First);
                logger.LogInformation(
                    "[StreamResolver] MP seed for {Url} listed {Count} chips; queueing each as a row",
                    stream.Url, chips.Others.Count + 1);
                return new ResolveOutcome(enriched, chips.Others, RequeueAsChip: true);
            }

            if (string.IsNullOrEmpty(m3u8Url))
            {
                V2StreamExpander.ApplySelectedChip(stream, m3u8Response?.SelectedStream);
                enriched.Status = StreamResolutionStatus.Failed;
                enriched.ErrorMessage = localLanPlayService.UsesRemoteCompute
                    ? localLanPlayService.LastRemoteComputeError ?? "No playlist from the paired computer"
                    : "No m3u8 URL returned from local LAN play service";
                logger.LogWarning("[StreamResolver] Failed to get m3u8 URL for {Channel}: {Error}",
                    stream.Channel, enriched.ErrorMessage);
                return new ResolveOutcome(enriched, SiblingsFrom(stream, m3u8Response));
            }

            V2StreamExpander.ApplySelectedChip(stream, m3u8Response?.SelectedStream);
            enriched.ResolvedM3U8Url = m3u8Url;
            enriched.Status = StreamResolutionStatus.Resolved;
            enriched.RequestHeaders = m3u8Response?.RequestHeaders;
            enriched.RewrittenSegments = m3u8Response?.RewrittenSegments?
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .ToList();
            enriched.Referer = ResolveReferer(m3u8Response, stream.Url);
            logger.LogInformation("[StreamResolver] Resolved m3u8 for {Channel}: {Url}", stream.Channel, m3u8Url);

            // Step 2: Health — when LocalService left soak headers, probe WITH those
            // headers (never bare CDN). That still rejects TikTok/.image/image/*
            // decoy segments. Blind soak-trust used to mark those Healthy.
            // One-shot burn on this GET is OK: RequiresFreshM3u8ForPlayback re-/plays
            // on attach.
            logger.LogInformation("[StreamResolver] Testing health and extracting metadata for {Channel}",
                stream.Channel);
            logger.LogInformation("[StreamResolver] Using referer for health check: {Referer}", enriched.Referer);
            var probe = TryLocalServiceHealthProbe(m3u8Response);
            if (LocalServiceSoakTrust.ShouldSkipClientCdnProbe(m3u8Response) && probe is null)
            {
                // Soak evidence without filterable headers — still build a probe from
                // raw RequestHeaders so we never fall through to a bare CDN GET.
                probe = new StreamHealthProbe
                {
                    RequestHeaders = FilterPlaybackHeaders(m3u8Response?.RequestHeaders)
                };
            }

            StreamHealth health = probe is null
                ? await healthChecker.CheckStreamHealthAsync(m3u8Url, enriched.Referer, cancellationToken)
                : await healthChecker.CheckStreamHealthAsync(m3u8Url, enriched.Referer, probe, cancellationToken);

            enriched.Health = health;
            if (health.Status == StreamHealthStatus.Healthy)
            {
                enriched.Status = StreamResolutionStatus.Healthy;
                logger.LogInformation("[StreamResolver] Stream {Channel} is healthy: {Quality}",
                    stream.Channel, health.GetQualityLabel());
            }
            else if (CdnReachabilityPolicy.IsCdnUnreachable(health.Status))
            {
                // Could not reach the CDN (connect timeout / refused / reset / TLS,
                // or a manifest 403/5xx). Never dead: deferred, never reported,
                // rechecked after a back-off while the game is live.
                enriched.Status = StreamResolutionStatus.Deferred;
                enriched.DeferReason = StreamDeferReason.CdnUnreachable;
                enriched.ErrorMessage = $"{CdnReachabilityPolicy.RecheckingMessage} ({health.Status})";
                logger.LogWarning("[StreamResolver] Stream {Channel}: {Error}", stream.Channel, enriched.ErrorMessage);
            }
            else
            {
                // Dead: manifest 404/410, host not resolved, empty/invalid
                // manifest, or a segment HTTP error / decoy bytes.
                enriched.Status = StreamResolutionStatus.Failed;
                enriched.ErrorMessage = $"Health check failed: {health.Status}";
                logger.LogWarning("[StreamResolver] Stream {Channel} failed health check: {Status}",
                    stream.Channel, health.Status);
            }

            return new ResolveOutcome(enriched, SiblingsFrom(stream, m3u8Response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            enriched.Status = StreamResolutionStatus.Failed;
            enriched.ErrorMessage = ex.Message;
            logger.LogError(ex, "[StreamResolver] Failed to resolve m3u8 URL for testing for {Channel} with {url}",
                stream.Channel, stream.Url);
            return new ResolveOutcome(enriched, []);
        }
    }

    private static readonly HashSet<string> LocalServicePlaybackHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "user-agent",
        "referer",
        "origin",
        "cookie",
        "authorization"
    };

    /// <summary>
    /// Build a health probe from LocalService soak data: rewritten MP segments
    /// and/or captured request headers (Cookie) from V1/FB <c>/play</c>.
    /// </summary>
    private static StreamHealthProbe? TryLocalServiceHealthProbe(M3U8Response? response)
    {
        if (response is null)
            return null;

        var rewritten = response.RewrittenSegments?
            .Where(MpRewrittenSegment.LooksLike)
            .ToList();
        var headers = FilterPlaybackHeaders(response.RequestHeaders);
        var hasRewritten = rewritten is { Count: > 0 };
        var hasHeaders = headers is { Count: > 0 };
        if (!hasRewritten && !hasHeaders)
            return null;

        return new StreamHealthProbe
        {
            RewrittenSegmentUrls = hasRewritten ? rewritten : null,
            RequestHeaders = hasHeaders ? headers : null
        };
    }

    private static Dictionary<string, string>? FilterPlaybackHeaders(
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return null;

        Dictionary<string, string>? filtered = null;
        foreach (var pair in headers)
        {
            if (string.IsNullOrWhiteSpace(pair.Value)
                || !LocalServicePlaybackHeaderNames.Contains(pair.Key))
            {
                continue;
            }

            filtered ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            filtered[pair.Key] = pair.Value;
        }

        return filtered;
    }

    /// <summary>
    /// Insert newly discovered MP chip siblings ahead of remaining catalog work
    /// so harvested labels resolve before later FB rows. Returns the rows added
    /// (duplicates of rows already queued are skipped).
    /// </summary>
    private static List<Stream> EnqueueMpSiblingsNext(
        List<Stream> pending,
        HashSet<int> started,
        ref int nextIndex,
        HashSet<string> seen,
        IReadOnlyList<Stream> siblings)
    {
        var added = siblings.Where(s => seen.Add(CandidateKey(s))).ToList();
        if (added.Count == 0)
            return added;

        InsertUnstarted(pending, started, added);
        nextIndex = FirstUnstarted(pending, started);
        return added;
    }

    /// <summary>Insert <paramref name="rows"/> at the first unstarted place, keeping <paramref name="started"/> aligned.</summary>
    private static void InsertUnstarted(List<Stream> pending, HashSet<int> started, IReadOnlyList<Stream> rows)
    {
        var insertAt = FirstUnstarted(pending, started);
        pending.InsertRange(insertAt, rows);
        ShiftStarted(started, insertAt, rows.Count);
    }

    private static void ShiftStarted(HashSet<int> started, int from, int by)
    {
        var shifted = started.Select(i => i >= from ? i + by : i).ToList();
        started.Clear();
        foreach (var i in shifted)
            started.Add(i);
    }

    private static int FirstUnstarted(List<Stream> pending, HashSet<int> started)
    {
        var i = 0;
        while (i < pending.Count && started.Contains(i))
            i++;
        return i;
    }

    /// <summary>
    /// An unlabeled MP seed's answer as chips: the label this row takes, and the other
    /// chips as new rows. Null for a labeled row, a non-MP row, no labels, or a
    /// playlist not tied to a listed label.
    /// </summary>
    private (string First, IReadOnlyList<Stream> Others)? ChipListSeed(Stream stream, M3U8Response? response)
    {
        if (response?.Streams is not { Count: > 0 }
            || !MpPageUrl.IsV2Stream(stream)
            || GetPlayerStreamName(stream) is not null)
        {
            return null;
        }

        var labels = chipNormalizer.Normalize(response.Streams).ToList();
        if (labels.Count == 0)
            return null;

        var selected = response.SelectedStream?.Trim();
        var match = labels.FirstOrDefault(l => string.Equals(l, selected, StringComparison.OrdinalIgnoreCase));

        // A playlist we cannot tie to a listed label (old LocalService, no selected
        // chip) is used as is: re-asking by label could miss it.
        if (!string.IsNullOrEmpty(response.Url) && match is null)
            return null;

        var first = match ?? labels[0];
        var rest = V2StreamExpander.RemainingFromLocalService(stream, labels, first, chipNormalizer);
        return (first, rest);
    }

    private static bool HasUnknownMpChipFanOut(Stream stream) =>
        MpPageUrl.IsV2Stream(stream)
        && string.IsNullOrWhiteSpace(stream.PlayerStream)
        && (stream.PlayerStreams is null || stream.PlayerStreams.Count == 0)
        && string.IsNullOrWhiteSpace(stream.Channel);

    private IReadOnlyList<Stream> SiblingsFrom(Stream source, M3U8Response? response)
    {
        if (response is null || !MpPageUrl.IsV2Stream(source))
        {
            return [];
        }

        return V2StreamExpander.RemainingFromLocalService(
            source,
            response.Streams,
            response.SelectedStream,
            chipNormalizer);
    }

    private static string? GetPlayerStreamName(Stream stream)
    {
        if (StreamCandidateRules.ShouldSkipCountdown(stream.IsCountdown))
        {
            return null;
        }

        // MP/v2: pass chip label when known. Catalog often has source=mp without resolutionStrategy=v2.
        if (!stream.RequiresV2StreamSelection
            && !string.Equals(stream.Source, "mp", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stream.ResolveCatalogSource(), "mp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(stream.PlayerStream))
        {
            return stream.PlayerStream.Trim();
        }

        if (!string.IsNullOrWhiteSpace(stream.Channel)
            && !string.Equals(stream.Channel, "V2", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stream.Channel, "V1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stream.Channel, "MP", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stream.Channel, "FB", StringComparison.OrdinalIgnoreCase))
        {
            return stream.Channel.Trim();
        }

        return null;
    }

    /// <summary>
    /// Asks the local service again (with backoff) while it is unavailable. A
    /// <c>null</c> answer is the stream's verdict and is returned at once.
    /// </summary>
    private async Task<M3U8Response?> ResolveWithInfrastructureRetryAsync(Stream stream, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ResolveM3U8ResponseInternalAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            // MP disabled / auth rejected won't clear in seconds: defer at once.
            catch (LocalServiceUnavailableException ex) when (attempt < InfrastructureRetryDelays.Length && ex.IsRetryable)
            {
                logger.LogInformation(
                    "[StreamResolver] Local service unavailable for {Channel} ({Reason}); retry {Attempt}/{Max} in {Delay}s",
                    stream.Channel, ex.Reason, attempt + 1, InfrastructureRetryDelays.Length,
                    InfrastructureRetryDelays[attempt].TotalSeconds);
                await DelayAsync(InfrastructureRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> GetM3U8UrlInternalAsync(Stream stream, CancellationToken cancellationToken)
    {
        var response = await ResolveM3U8ResponseInternalAsync(stream, cancellationToken);
        return response?.Url;
    }

    private async Task<M3U8Response?> ResolveM3U8ResponseInternalAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var playerStreamName = GetPlayerStreamName(stream);
            logger.LogInformation(
                "[StreamResolver] Fetching M3U8 from local LAN service for source {Url}{StreamSuffix}",
                stream.Url,
                playerStreamName is null ? "" : $" (stream={playerStreamName})");
            var result = await localLanPlayService.ResolveM3U8UrlAsync(
                stream.Url,
                playerStreamName,
                stream.ResolutionStrategy,
                stream.Source,
                cancellationToken);
            logger.LogInformation("[StreamResolver] M3U8 resolve completed for source {Url}", stream.Url);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Not our cancel (e.g. a shared /mp harvest cancelled under another caller):
            // that says nothing about the stream. Infrastructure, so retried/Deferred,
            // never Failed (rule 13).
            throw new LocalServiceUnavailableException(
                LocalServiceUnavailableReason.CallTimeout,
                $"Resolve for {stream.Url} was cancelled by another caller", ex);
        }
        catch (LocalServiceUnavailableException)
        {
            // Infrastructure, not the stream: the caller decides (retry / defer).
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[StreamResolver] Failed to fetch M3U8 for {Url}", stream.Url);
            return null;
        }
    }

    private static string ResolveReferer(M3U8Response? response, string fallbackUrl)
    {
        var capturedReferer = GetHeaderValue(response?.RequestHeaders, "referer");
        return string.IsNullOrWhiteSpace(capturedReferer) ? fallbackUrl : capturedReferer;
    }

    private static string? GetHeaderValue(IReadOnlyDictionary<string, string>? headers, string headerName)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        foreach (var pair in headers)
        {
            if (pair.Key.Equals(headerName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value;
            }
        }

        return null;
    }
}