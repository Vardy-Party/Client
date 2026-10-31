using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using VardyParty.Ports;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

public class StreamResolutionOrchestrator(
    IApiService apiService,
    IStreamResolver streamResolver,
    IStreamSelectionCoordinator selectionCoordinator,
    IStreamHealthService streamHealthService,
    IStreamHealthReporter streamHealthReporter,
    ISessionIdProvider sessionIdProvider,
    SelectionState selectionState,
    IStreamSwitchingService streamSwitchingService,
    ILogger<StreamResolutionOrchestrator> logger,
    ILocalLanPlayService? localLanPlayService = null,
    IRemoteComputeKeepAlive? keepAlive = null,
    WarmPlaybackUrlCache? warmUrls = null,
    IUpcomingStreamPrewarmer? prewarmer = null) : IStreamResolutionOrchestrator
{
    private static readonly TimeSpan PlaybackHealthInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits before each re-queue pass over rows the resolver deferred because
    /// the local service was unavailable. Deferred rows are never reported or
    /// dropped; they get these extra passes after the main sweep.
    /// </summary>
    internal static readonly TimeSpan[] DeferredRequeueDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    /// <summary>Test seam for the re-queue waits.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    /// <summary>Test seam for the CDN-unreachable recheck waits (2 min, 5 min, then 10 min).</summary>
    internal Func<TimeSpan, CancellationToken, Task> CdnRecheckDelayAsync { get; set; } = Task.Delay;

    /// <summary>Test seam for the clock that decides whether the game is still live for rechecks.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// How long a new StartAsync waits for a previous session that is still
    /// running and was not cancelled. When this runs out the outcome is
    /// StartRefused — surfaced to the user — never a silent empty outcome.
    /// A predecessor whose token is already cancelled is unwinding. Wait up to
    /// <see cref="CancelledUnwindWait"/> for that gate, then refuse. The cap
    /// covers a few seconds of /play unwind and does not hang the next pick.
    /// </summary>
    private static readonly TimeSpan StartGateWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan CancelledUnwindWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Shown for the whole paired search. A mobile link can sit on the second
    /// page for minutes before chips appear, and the title "Finding streams"
    /// alone looked like a stall.
    /// </summary>
    public const string PairedFindingStatus =
        "Using the paired computer. A slow link can take several minutes.";

    private readonly BehaviorSubject<StreamResolutionProgress> _progressSubject =
        new(new StreamResolutionProgress());
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private ActiveSession? _activeSession;

    private bool _hasBufferingOccurred;
    private int _healthyStreamCount;
    private bool _isResolving;
    private string _status = string.Empty;
    private int _streamsTested;

    private int _totalStreams;

    public IObservable<StreamResolutionProgress> ProgressUpdated => _progressSubject;

    public async Task<StreamResolutionOutcome> StartAsync(
        Game game,
        IPlaybackLauncher launcher,
        CancellationToken cancellationToken = default)
    {
        var acquired = await _startGate.WaitAsync(StartGateWait, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            // Hosts cancel the previous session before calling again. That
            // session can still hold the gate while /play unwinds. Refusing
            // after 2s latches the homepage on "still finishing". Wait it out
            // on the caller token. A session that was NOT cancelled is still
            // active — refuse, so two resolves never run together.
            var active = Volatile.Read(ref _activeSession);
            if (active?.Token.IsCancellationRequested == true)
            {
                acquired = await _startGate.WaitAsync(CancelledUnwindWait, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                acquired = await _startGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!acquired)
        {
            logger.LogWarning("[StreamResolution] Refusing StartAsync for {Home} vs {Away} - previous session still active",
                game.DisplayHome, game.DisplayAway);
            return new StreamResolutionOutcome { StartRefused = true };
        }

        var session = new ActiveSession(cancellationToken);
        Volatile.Write(ref _activeSession, session);
        try
        {
            return await StartCoreAsync(game, launcher, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Release first so a waiter whose 2s timeout lands in this gap
            // still observes the cancelled token and takes the unwind wait,
            // instead of a null session and an immediate StartRefused.
            _startGate.Release();
            Interlocked.CompareExchange(ref _activeSession, null, session);
        }
    }

    private async Task<StreamResolutionOutcome> StartCoreAsync(
        Game game,
        IPlaybackLauncher launcher,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveAsync(game, launcher, cancellationToken);
        }
        finally
        {
            // Whatever exit fired (user closed, cancelled, failed, completed), the homepage modal is bound
            // to IsResolving, so it must never depend on which return statement ran.
            if (_isResolving)
            {
                _isResolving = false;
                PublishProgress();
            }
        }
    }

    private async Task<StreamResolutionOutcome> ResolveAsync(
        Game game,
        IPlaybackLauncher launcher,
        CancellationToken cancellationToken)
    {
        Reset();
        streamSwitchingService.Initialize(game.ApiLeague, game.Home, game.Away);

        _isResolving = true;
        _status = localLanPlayService?.UsesRemoteCompute == true
            ? PairedFindingStatus
            : "Searching for streams...";
        PublishProgress();

        var outcome = new StreamResolutionOutcome();
        var hasPlayedFirstStream = false;
        var streamCount = 0;
        Task<PlaybackResult?>? playbackTask = null;

        if (localLanPlayService != null
            && !localLanPlayService.UsesRemoteCompute
            && !await localLanPlayService.IsAvailableAsync(cancellationToken))
        {
            logger.LogWarning("[StreamResolution] Local LAN service is unavailable — aborting stream resolution");
            _status = "Local service unavailable";
            outcome.LocalServiceUnavailable = true;
            _isResolving = false;
            PublishProgress();
            return outcome;
        }

        await selectionCoordinator.InitializeAsync(game, cancellationToken);
        var orderedCandidates = selectionCoordinator.GetOrderedCandidates();
        var orderedStreams = orderedCandidates.Select(c => c.Stream).ToList();
        // First play follows catalog order. FillWindow may finish a later FB
        // while an earlier /mp is still in flight; hold that FB until every
        // lower index has yielded so playback still starts on the earlier row.
        var catalogIndex = new Dictionary<StreamModel, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < orderedStreams.Count; i++)
            catalogIndex[orderedStreams[i]] = i;
        var resolvedCatalog = new HashSet<int>();
        var heldHealthy = new SortedDictionary<int, EnrichedStream>();
        var nextExtraIndex = orderedStreams.Count;

        // Candidate testing must stop the moment the user closes the player. Without this the loop below
        // only noticed the close when the resolver next yielded a stream, so after Back the homepage kept
        // showing the "Finding streams" modal for as long as the remaining candidates took to test.
        using var enumerationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // A first play that fails (an empty /play, a start that never established)
        // moves straight on to the next pool row inside RunPlaybackChainAsync. When
        // the pool is empty it sets this flag, and the next healthy row to join the
        // pool starts playback at once - the "Finding streams... N healthy" dialog
        // no longer waits for the whole sweep to finish. The lock pairs the pool add
        // with the flag check so a row is never added between "pool empty" and
        // "flag set" without starting playback.
        var playbackLock = new object();
        var awaitingPlayableRow = false;

        void StartPlaybackChain(EnrichedStream first)
        {
            playbackTask = RunPlaybackChainAsync(
                game,
                first,
                launcher,
                () => awaitingPlayableRow = true,
                playbackLock,
                cancellationToken);
            _ = CancelEnumerationWhenViewerLeaves(playbackTask, enumerationCts);
        }

        void FlushReadyForPlayback()
        {
            var frontier = 0;
            while (frontier < orderedStreams.Count && resolvedCatalog.Contains(frontier))
                frontier++;

            // Catalog rows stay held until the frontier passes (first play follows
            // catalog order). MP chip siblings use nextExtraIndex (>= catalog
            // count); holding them until every catalog row finishes left the UI
            // on one V2 stream while /mp had already harvested the rest.
            var ready = heldHealthy
                .Where(pair =>
                    pair.Key < frontier
                    || frontier >= orderedStreams.Count
                    || (pair.Key >= orderedStreams.Count && hasPlayedFirstStream))
                .Select(pair => pair.Key)
                .ToList();
            foreach (var index in ready)
            {
                var readyStream = heldHealthy[index];
                heldHealthy.Remove(index);
                EnrichedStream? startOn = null;
                lock (playbackLock)
                {
                    streamSwitchingService.AddHealthyStream(readyStream);
                    if (!hasPlayedFirstStream)
                    {
                        hasPlayedFirstStream = true;
                        startOn = readyStream;
                    }
                    else if (awaitingPlayableRow)
                    {
                        // Every earlier row failed and the pool ran dry: play the row
                        // that now holds the pool index (the one just added, unless it
                        // merged into an existing entry as a fallback).
                        awaitingPlayableRow = false;
                        startOn = streamSwitchingService.GetCurrentStream() ?? readyStream;
                    }
                }

                _healthyStreamCount = streamSwitchingService.GetHealthyStreams().Count;
                PublishProgress();

                if (startOn != null)
                    StartPlaybackChain(startOn);
            }
        }

        if (orderedStreams.Count == 0)
        {
            _status = "No streams found";
            outcome.NoWorkingStreams = true;
            _isResolving = false;
            PublishProgress();
            return outcome;
        }

        // LAN discovery stays the fast path. The foreground service is only
        // held while this session is actually resolving through the paired host,
        // and it stays up across every candidate in the loop below.
        var relayed = localLanPlayService?.UsesRemoteCompute == true
            && !await localLanPlayService.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        using var relayKeepAlive = RelayKeepAliveHold.Start(keepAlive, relayed);

        try
        {
            var deferred = new List<StreamModel>();
            var cdnDeferred = new List<StreamModel>();
            var deferredIndex = new Dictionary<StreamModel, int>(ReferenceEqualityComparer.Instance);

            // Returns true when the viewer has left and the outcome is final.
            async Task<bool> ViewerLeftAsync()
            {
                if (playbackTask == null || !playbackTask.IsCompleted)
                    return false;

                var playbackResult = await playbackTask;
                if (playbackResult?.ViewerLeft != true)
                    return false;

                outcome.UserClosed = true;
                outcome.PlaybackResult = playbackResult;
                return true;
            }

            void Accept(EnrichedStream enrichedStream, bool requeued)
            {
                if (!requeued)
                {
                    streamCount++;
                    _streamsTested++;
                    PublishProgress();
                }

                int index;
                if (deferredIndex.Remove(enrichedStream.Stream, out var earlier))
                    index = earlier;
                else if (catalogIndex.TryGetValue(enrichedStream.Stream, out var known))
                    index = known;
                else
                    index = nextExtraIndex++;

                // A deferred row still counts as "done" for the first-play
                // frontier, so one unreachable /play can't hold back a healthy
                // later row.
                resolvedCatalog.Add(index);

                switch (enrichedStream.Status)
                {
                    case StreamResolutionStatus.Failed:
                        _ = ReportHealthAsync(game, enrichedStream.Stream, "failed", enrichedStream.ErrorMessage,
                            enrichedStream.Health, cancellationToken);
                        break;
                    case StreamResolutionStatus.Healthy:
                        // The client's own probe passed: report "working". It used to
                        // send "unknown", which looked like no verdict.
                        _ = ReportHealthAsync(game, enrichedStream.Stream, "working", null, enrichedStream.Health,
                            cancellationToken);
                        heldHealthy[index] = enrichedStream;
                        break;
                    case StreamResolutionStatus.Deferred:
                        // An infrastructure failure: nothing about the stream is
                        // known. Don't report it and don't drop it; re-queue it.
                        // A CDN-unreachable row waits minutes, not seconds: it goes to
                        // the CDN recheck (CdnReachabilityPolicy), not the 5s/15s passes.
                        deferredIndex[enrichedStream.Stream] = index;
                        if (enrichedStream.DeferReason == StreamDeferReason.CdnUnreachable)
                            cdnDeferred.Add(enrichedStream.Stream);
                        else
                            deferred.Add(enrichedStream.Stream);
                        break;
                }

                // Start playback without awaiting so stream testing can continue.
                FlushReadyForPlayback();
            }

            await foreach (var enrichedStream in streamResolver.ResolveStreamsIncrementallyAsync(
                               orderedStreams,
                               2,
                               enumerationCts.Token,
                               totalCount =>
                               {
                                   _totalStreams = totalCount;
                                   PublishProgress();
                               }).WithCancellation(enumerationCts.Token))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ViewerLeftAsync())
                    return outcome;

                Accept(enrichedStream, requeued: false);
            }

            FlushReadyForPlayback();

            for (var pass = 0; pass < DeferredRequeueDelays.Length && deferred.Count > 0; pass++)
            {
                var retry = deferred.ToList();
                deferred.Clear();
                logger.LogInformation(
                    "[StreamResolution] Re-queueing {Count} stream(s) deferred by local-service unavailability (pass {Pass}/{Max}) in {Delay}s",
                    retry.Count, pass + 1, DeferredRequeueDelays.Length, DeferredRequeueDelays[pass].TotalSeconds);
                await DelayAsync(DeferredRequeueDelays[pass], enumerationCts.Token).ConfigureAwait(false);
                if (await ViewerLeftAsync())
                    return outcome;

                await foreach (var enrichedStream in streamResolver.ResolveStreamsIncrementallyAsync(
                                   retry, 2, enumerationCts.Token).WithCancellation(enumerationCts.Token))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await ViewerLeftAsync())
                        return outcome;

                    Accept(enrichedStream, requeued: true);
                }

                FlushReadyForPlayback();
            }

            outcome.DeferredStreamCount = deferred.Count;
            if (deferred.Count > 0)
            {
                logger.LogWarning(
                    "[StreamResolution] {Count} stream(s) still deferred after re-queue passes; local service unavailable (not reported, not dropped)",
                    deferred.Count);
            }

            // CDN unreachable is never dead: recheck those rows after 2 min, 5 min,
            // then every 10 min while the game is live. A row that comes back Healthy
            // joins the pool through Accept (appended, stable numbering). Playback
            // that is already running carries on meanwhile.
            if (cdnDeferred.Count > 0)
            {
                var viewerLeft = await CdnUnreachableRecheckLoop.RunWhileSessionLiveAsync(
                    cdnDeferred,
                    (rows, token) => streamResolver.ResolveStreamsIncrementallyAsync(rows, 2, token),
                    row => Accept(row, requeued: true),
                    () => playbackTask,
                    ViewerLeftAsync,
                    () => CdnReachabilityPolicy.ShouldKeepRechecking(game, UtcNow()),
                    CdnRecheckDelayAsync,
                    (count, wait) =>
                    {
                        if (playbackTask is { IsCompleted: false })
                            return;
                        _status = $"{CdnReachabilityPolicy.RecheckingMessage}: {count} stream(s), next check in {wait.TotalMinutes:F0} min";
                        PublishProgress();
                    },
                    logger,
                    enumerationCts.Token).ConfigureAwait(false);
                if (viewerLeft)
                    return outcome;

                cancellationToken.ThrowIfCancellationRequested();
                outcome.CdnUnreachableStreamCount = cdnDeferred.Count;
            }
        }
        catch (OperationCanceledException) when (playbackTask is { IsCompleted: true })
        {
            // Close cancels the resolve CTS while PlayVideoAsync already completed
            // with a viewer-left result - prefer that outcome over a bare cancel.
            var playbackResult = await playbackTask;
            outcome.PlaybackResult = playbackResult;
            if (playbackResult?.ViewerLeft == true)
            {
                outcome.UserClosed = true;
            }

            _isResolving = false;
            PublishProgress();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            _isResolving = false;
            PublishProgress();
            throw;
        }

        // Wait for playback to complete if it was started
        if (playbackTask != null)
        {
            var playbackResult = await playbackTask;
            outcome.PlaybackResult = playbackResult;

            if (playbackResult?.ViewerLeft == true)
            {
                outcome.UserClosed = true;
            }
            else if (playbackResult is { Success: false, IsLocalServiceUnavailable: true })
            {
                // The local service gave no verdict on the fresh /play even after
                // the infrastructure retries. That says nothing about the stream:
                // no "failed" report, the row stays in the pool, and the outcome
                // says "local service unavailable" (same as deferred rows).
                logger.LogWarning("[StreamResolution] Playback deferred - {Message}", playbackResult.Message);
                _status = playbackResult.IsCdnUnreachable
                    ? PlaybackCloseReasons.CdnUnreachable
                    : PlaybackCloseReasons.LocalServiceUnavailable;
                outcome.LocalServiceUnavailable = !playbackResult.IsCdnUnreachable;
                PublishProgress();
            }
            else if (playbackResult != null && !playbackResult.Success)
            {
                // RunPlaybackChainAsync already reported the failed row, removed it and
                // played the next one; it only ends on a failure when the pool is empty.
                selectionCoordinator.ResumeTesting();
                _status = "Testing resumed";
                PublishProgress();
            }
        }

        if (_healthyStreamCount == 0)
        {
            var remoteError = localLanPlayService?.LastRemoteComputeError;
            if (!string.IsNullOrWhiteSpace(remoteError))
            {
                _status = remoteError;
                outcome.RemoteComputeError = remoteError;
            }
            else if (outcome.CdnUnreachableStreamCount > 0 && outcome.DeferredStreamCount == 0)
            {
                // The CDN could not be reached (IP block, CDN down). The rows were
                // not reported or dropped; the local service itself was fine.
                _status = CdnReachabilityPolicy.RecheckingMessage;
                outcome.NoWorkingStreams = true;
            }
            else if (outcome.DeferredStreamCount > 0)
            {
                // Rows were never answered: the local service, not the streams.
                _status = "Local service unavailable";
                outcome.LocalServiceUnavailable = true;
            }
            else if (localLanPlayService?.UsesRemoteCompute == true)
            {
                _status = "No working streams found";
                outcome.NoWorkingStreams = true;
            }
            else if (localLanPlayService != null && !await localLanPlayService.IsAvailableAsync(cancellationToken))
            {
                _status = "Local service unavailable";
                outcome.LocalServiceUnavailable = true;
            }
            else
            {
                _status = "No working streams found";
                outcome.NoWorkingStreams = true;
            }
            PublishProgress();
        }

        _isResolving = false;
        PublishProgress();
        logger.LogInformation("[StreamResolution] Completed. Total={Total}, Tested={Tested}, Healthy={Healthy}",
            streamCount, _streamsTested, _healthyStreamCount);
        return outcome;
    }

    /// <summary>
    /// Stops candidate testing the moment the viewer leaves the player (user close or the player window
    /// going away). Returns the continuation so callers and tests can await it deterministically.
    /// </summary>
    internal static Task CancelEnumerationWhenViewerLeaves(Task<PlaybackResult?> playbackTask, CancellationTokenSource enumerationCts) =>
        playbackTask.ContinueWith(
            t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || t.Result?.ViewerLeft != true) return;
                try { enumerationCts.Cancel(); }
                catch (ObjectDisposedException) { }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

    public void Reset()
    {
        _totalStreams = 0;
        _streamsTested = 0;
        _healthyStreamCount = 0;
        _isResolving = false;
        _status = string.Empty;
        _progressSubject.OnNext(new StreamResolutionProgress());
    }

    public Task ReportCurrentStreamAsBadAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        var currentStream = streamSwitchingService.GetCurrentStream();
        if (currentStream == null)
        {
            return Task.CompletedTask;
        }

        return streamHealthReporter.ReportBadStreamAsync(
            currentStream.Stream.Url,
            currentStream.Stream.Url,
            StreamHealthIdentity.GetStreamName(currentStream.Stream),
            reason,
            cancellationToken);
    }

    private Task ReportHealthAsync(
        Game game,
        StreamModel stream,
        string status,
        string? error,
        StreamHealth? health = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stream.Url))
        {
            return Task.CompletedTask;
        }

        var report = new StreamHealthReport
        {
            StreamUrl = stream.Url,
            StreamName = StreamHealthIdentity.GetStreamName(stream),
            Status = status,
            Error = error,
            Bitrate = health?.Bitrate,
            Resolution = health?.Resolution,
            Framerate = health?.FrameRate,
            VideoCodec = health?.VideoCodec,
            AudioCodec = health?.AudioCodec,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionId = sessionIdProvider.SessionId
        };

        return streamHealthService.ReportHealthAsync(game.ApiLeague, game.Home, game.Away, report, cancellationToken);
    }

    private async Task<PlaybackResult?> PlayStreamAsync(
        Game game,
        EnrichedStream enrichedStream,
        IPlaybackLauncher launcher,
        CancellationToken cancellationToken,
        PlayAttempt? attempt = null)
    {
        // FB /play tokens are one-shot: find-phase URLs must not be reused for
        // ExoPlayer. Soak rows always re-/play; others keep the cached fast path
        // with a single fresh retry on failure below.
        // One-shot rows whose token expiry is readable with comfortable life left (and
        // that are not known single-use) play the cached URL; a failed start re-/plays once.
        var oneShot = LocalServiceSoakTrust.RequiresFreshM3u8ForPlayback(enrichedStream)
            && !PlaybackUrlReusePolicy.CanReuseCached(enrichedStream, DateTimeOffset.UtcNow);
        bool usedCachedUrl = !oneShot && !string.IsNullOrEmpty(enrichedStream.ResolvedM3U8Url);
        string? m3u8Url;
        if (usedCachedUrl)
        {
            logger.LogInformation("[StreamResolution] Using cached M3U8 for playback: {Channel}", enrichedStream.Stream.Channel);
            m3u8Url = enrichedStream.ResolvedM3U8Url;
        }
        else
        {
            logger.LogInformation(
                oneShot
                    ? "[StreamResolution] Resolving fresh one-shot M3U8 for playback: {Channel}"
                    : "[StreamResolution] Resolving fresh M3U8 for playback: {Channel}",
                enrichedStream.Stream.Channel);
            try
            {
                m3u8Url = await ResolveFreshPlaybackOntoAsync(enrichedStream, cancellationToken);
            }
            catch (InfrastructureUnavailableException ex)
            {
                return LocalServiceUnavailableResult(enrichedStream, ex);
            }
        }

        if (string.IsNullOrEmpty(m3u8Url))
        {
            logger.LogWarning("[StreamResolution] Failed to resolve m3u8 for {Channel}", enrichedStream.Stream.Channel);
            return PlaybackResult.Completed("Failed to resolve m3u8", true);
        }

        var title = $"{game.DisplayHome} vs {game.DisplayAway}";
        using var playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        selectionState.CurrentStream = enrichedStream.Stream;

        // Reset buffering flag and subscribe to buffering events
        _hasBufferingOccurred = false;
        EventHandler<bool>? bufferingHandler = (sender, isBuffering) =>
        {
            if (isBuffering)
            {
                _hasBufferingOccurred = true;
            }
        };
        launcher.BufferingStateChanged += bufferingHandler;

        var reportingTask = StartPlaybackHealthReportingAsync(game, enrichedStream.Stream, launcher, playbackCts.Token);
        PlaybackResult? result;
        // One-shot V1 rows: keep the next 1-2 warm (single-flight) for the whole session.
        using var prewarmLoop = prewarmer?.Start(playbackCts.Token);
        try
        {
            // Warm the next candidate while this one plays so the first Next click is instant.
            PrefetchUpcomingStreamUrl(game, cancellationToken);

            if (attempt != null)
                attempt.PlayerOpened = true;
            result = await launcher.PlayVideoAsync(
                m3u8Url,
                string.IsNullOrWhiteSpace(enrichedStream.Referer) ? enrichedStream.Stream.Url : enrichedStream.Referer,
                title,
                () => HandleNextStreamRequestedAsync(game, cancellationToken),
                game.DisplayLeague,
                game.DisplayHome,
                game.DisplayAway,
                enrichedStream.RequestHeaders);
        }
        finally
        {
            launcher.BufferingStateChanged -= bufferingHandler;
            playbackCts.Cancel();
            try
            {
                await reportingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        // If we used a cached URL and it failed immediately, the token may have been
        // invalidated. Re-resolve fresh and retry this same stream once.
        if (usedCachedUrl && result != null && !result.Success &&
            !result.ViewerLeft && !result.IsLocalServiceUnavailable)
        {
            logger.LogInformation("[StreamResolution] Cached URL failed for {Channel} — retrying with fresh M3U8", enrichedStream.Stream.Channel);
            string? freshUrl;
            try
            {
                freshUrl = await ResolveFreshPlaybackOntoAsync(enrichedStream, cancellationToken);
            }
            catch (InfrastructureUnavailableException ex)
            {
                // The cached token failed and the service could not mint a new one.
                // Without a fresh verdict the stream is not proven dead: defer.
                return LocalServiceUnavailableResult(enrichedStream, ex);
            }
            if (StreamCandidateRules.ShouldAcceptFreshM3U8(m3u8Url, freshUrl))
            {
                // Retry playback with the fresh URL — usedCachedUrl=false so no further retry loop.
                return await PlayStreamAsync(game, enrichedStream, launcher, cancellationToken, attempt);
            }
            logger.LogWarning("[StreamResolution] Fresh M3U8 unavailable or identical for {Channel} — treating as failed", enrichedStream.Stream.Channel);
        }

        return result;
    }

    /// <summary>
    /// Full <c>/play</c> resolve onto the pool row — URL plus Referer/headers/segments —
    /// so first attach matches chip-switch fresh-resolve behavior.
    /// </summary>
    private async Task<string?> ResolveFreshPlaybackOntoAsync(
        EnrichedStream enrichedStream,
        CancellationToken cancellationToken)
    {
        var stream = enrichedStream.Stream;
        if (stream is null)
            return null;

        if (warmUrls != null)
        {
            await warmUrls.WaitForInFlightAsync(enrichedStream, cancellationToken);
            if (warmUrls.TryTake(enrichedStream, DateTimeOffset.UtcNow, out var warm))
            {
                logger.LogInformation("[StreamResolution] Using warm pre-resolved M3U8 for {Channel}", stream.Channel);
                EnrichedStreamM3U8Apply.Apply(enrichedStream, warm);
                return warm.Url;
            }
        }

        M3U8Response? response;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                response = await apiService.GetM3U8UrlAsync(
                    stream.Url,
                    EnrichedStreamM3U8Apply.PlayerStreamNameForPlayback(stream),
                    stream.ResolutionStrategy,
                    stream.Source,
                    cancellationToken);
                break;
            }
            catch (InfrastructureUnavailableException ex)
                when (InfrastructureRetryPolicy.ShouldRetryFreshResolve(ex, attempt))
            {
                // A blip (unreachable, our timeout, LS 5xx): ask again, same as
                // the find phase. MP disabled / auth skip straight to the caller.
                var delay = InfrastructureRetryPolicy.FreshResolveDelays[attempt];
                logger.LogWarning(
                    "[StreamResolution] Fresh resolve for {Channel} hit local service unavailable ({Message}) - retry {Attempt} in {Delay}s",
                    stream.Channel, ex.Message, attempt + 1, delay.TotalSeconds);
                await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        if (response is null || string.IsNullOrWhiteSpace(response.Url))
            return null;

        EnrichedStreamM3U8Apply.Apply(enrichedStream, response);
        return response.Url;
    }

    private async Task StartPlaybackHealthReportingAsync(
        Game game,
        StreamModel stream,
        IPlaybackLauncher launcher,
        CancellationToken cancellationToken)
    {
        try
        {
            // Report the row that is playing NOW. The native session can
            // make-before-break onto another pool row, and this loop used to keep
            // reporting the first stream's URL long after it had been switched away.
            StreamModel Playing() => streamSwitchingService.GetCurrentStream()?.Stream ?? stream;
            var softRefreshTicks = 0;

            // Wait for player to initialize and extract metadata
            // Windows MediaPlayer fires NaturalVideoSizeChanged after video decodes (~1-2s)
            await Task.Delay(TimeSpan.FromMilliseconds(2500), cancellationToken);

            // Get real metrics from the player service after playback has started
            var initialMetrics = launcher.GetCurrentMetrics();
            var started = Playing();
            _ = streamHealthReporter.ReportPlaybackStartedAsync(started.Url, null,
                StreamHealthIdentity.GetStreamName(started), initialMetrics, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PlaybackHealthInterval, cancellationToken);

                var metrics = launcher.GetCurrentMetrics();
                if (metrics != null)
                {
                    metrics.IsBuffering = _hasBufferingOccurred;
                    _hasBufferingOccurred = false;
                }

                // Report periodic metrics without duplicating metadata
                var playing = Playing();
                _ = streamHealthReporter.ReportPlaybackMetricsAsync(playing.Url, null,
                    StreamHealthIdentity.GetStreamName(playing), metrics, cancellationToken);

                // Soft-refresh recommendations about every 60s (every other 30s tick).
                softRefreshTicks++;
                if (softRefreshTicks % 2 == 0)
                {
                    _ = EnsureFreshRecommendationsAsync(game, force: false, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleNextStreamRequestedAsync(Game game, CancellationToken cancellationToken)
    {
        // Stable channel numbering (docs/STREAM_PLAYBACK_RULES.md): Next walks the
        // numbered pool in order and Next on the last channel goes to channel 1.
        // Recommendations never re-sort the pool or hijack Next; on wraparound they
        // are only refreshed (chips and the first pick use them).
        if (streamSwitchingService.WouldWrapOnNext())
        {
            _ = await EnsureFreshRecommendationsAsync(game, force: true, cancellationToken);
        }

        var nextStream = streamSwitchingService.GetNextHealthyStream();
        if (nextStream == null)
        {
            logger.LogInformation("[StreamResolution] No next stream available");
            _status = "No next stream available";
            PublishProgress();
            return;
        }

        // Use the prefetched URL if available (instant). If not, resolve fresh now.
        // The caller (PlayStreamAsync) will retry with a fresh URL if the cached one 403s.
        if (string.IsNullOrEmpty(nextStream.ResolvedM3U8Url)
            && LocalServiceSoakTrust.RequiresFreshM3u8ForPlayback(nextStream))
        {
            // One-shot row: the player's make-before-break switch resolves (or takes the
            // warm URL) on attach - resolving here too would be a second /play.
            logger.LogInformation("[StreamResolution] Next onto one-shot {Channel} - player resolves on attach",
                nextStream.Stream.Channel);
        }
        else if (string.IsNullOrEmpty(nextStream.ResolvedM3U8Url))
        {
            _status = "Switch requested - resolving stream URL...";
            PublishProgress();

            string? resolved;
            try
            {
                resolved = await apiService.ResolveM3U8ForPlaybackAsync(
                    nextStream.Stream,
                    $"/{game.ApiLeague}/{game.Home}/{game.Away}",
                    cancellationToken);
            }
            catch (InfrastructureUnavailableException ex)
            {
                // Keep the current stream and the next row: nothing was proven dead.
                logger.LogWarning("[StreamResolution] Next stream {Channel} deferred - local service unavailable: {Message}",
                    nextStream.Stream.Channel, ex.Message);
                _status = "Switch failed - local service unavailable";
                PublishProgress();
                return;
            }

            if (string.IsNullOrEmpty(resolved))
            {
                logger.LogWarning("[StreamResolution] Failed to resolve m3u8 for next stream {Channel}",
                    nextStream.Stream.Channel);
                _status = "Switch failed - could not resolve stream URL";
                PublishProgress();
                return;
            }

            nextStream.ResolvedM3U8Url = resolved;
            logger.LogInformation("[StreamResolution] Resolved fresh M3U8 for next stream {Channel}",
                nextStream.Stream.Channel);
        }
        else
        {
            logger.LogInformation("[StreamResolution] Using prefetched M3U8 for next stream {Channel}",
                nextStream.Stream.Channel);
        }

        if (streamSwitchingService.SwitchToNextStream())
        {
            logger.LogInformation("[StreamResolution] Switched to next stream");
            _status = "Switched to next stream";
            PublishProgress();
            PrefetchUpcomingStreamUrl(game, cancellationToken);
        }
    }

    private async Task<RecommendationResponse?> EnsureFreshRecommendationsAsync(
        Game game,
        bool force,
        CancellationToken cancellationToken)
    {
        var cached = selectionCoordinator.LatestRecommendations;
        if (!force
            && !StreamRecommendationPolicy.IsStale(
                cached,
                DateTimeOffset.UtcNow,
                StreamRecommendationPolicy.HardRefreshMaxAge))
        {
            return cached;
        }

        try
        {
            var fresh = await streamHealthService.GetRecommendationsAsync(
                game.ApiLeague,
                game.Home,
                game.Away,
                cancellationToken);
            if (fresh != null)
            {
                selectionCoordinator.RememberRecommendations(fresh);
                logger.LogInformation(
                    "[StreamResolution] Refreshed recommendations (force={Force}, count={Count}, generatedAt={GeneratedAt})",
                    force,
                    fresh.Recommended?.Count ?? 0,
                    fresh.GeneratedAt);
            }

            return fresh ?? cached;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[StreamResolution] Recommendation refresh failed");
            return cached;
        }
    }

    /// <summary>
    /// Warm the next candidate's m3u8 in the background so the following Next click stays
    /// instant without relying on a long-lived health-check URL.
    /// </summary>
    private void PrefetchUpcomingStreamUrl(Game game, CancellationToken cancellationToken)
    {
        var current = streamSwitchingService.GetCurrentStream();
        var upcoming = streamSwitchingService.GetNextHealthyStream();
        // With a single candidate, "next" wraps to current — don't re-resolve the live stream.
        if (upcoming?.Stream == null || ReferenceEquals(upcoming, current)) return;

        // One-shot rows: every attach re-/plays or takes a warm URL, so a bare
        // ResolveM3U8ForPlaybackAsync write-back here was a wasted second /play per
        // switch (Android ignored it). The single-flight pre-resolver owns these rows.
        if (LocalServiceSoakTrust.RequiresFreshM3u8ForPlayback(upcoming))
        {
            prewarmer?.Kick();
            return;
        }

        var channel = upcoming.Stream.Channel;
        var path = $"/{game.ApiLeague}/{game.Home}/{game.Away}";
        var stream = upcoming.Stream;

        _ = Task.Run(async () =>
        {
            try
            {
                var resolved = await apiService.ResolveM3U8ForPlaybackAsync(stream, path, cancellationToken);
                if (string.IsNullOrEmpty(resolved))
                {
                    logger.LogDebug(
                        "[StreamResolution] Prefetch skipped — no m3u8 for upcoming {Channel}",
                        channel);
                    return;
                }

                // Only write back if this is still the upcoming candidate (user may have switched).
                var stillUpcoming = streamSwitchingService.GetNextHealthyStream();
                if (stillUpcoming?.Stream == stream)
                {
                    stillUpcoming.ResolvedM3U8Url = resolved;
                    logger.LogInformation(
                        "[StreamResolution] Prefetched fresh M3U8 for upcoming stream {Channel}",
                        channel);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "[StreamResolution] Prefetch failed for upcoming {Channel}",
                    channel);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Plays <paramref name="first"/>, and while a start fails on the stream itself
    /// (an empty <c>/play</c>, or a player session that ended without playing) reports
    /// that row once, removes it and plays the row that takes its pool index. It runs
    /// alongside candidate testing, so a failed first play no longer waits for the
    /// whole sweep before the next healthy row is tried.
    /// <para>
    /// Ends on success, the viewer leaving, or a local-service failure (no report, no
    /// removal: the row stays for a later attempt). With an empty pool it calls
    /// <paramref name="onPoolEmpty"/> under <paramref name="playbackLock"/>, so the next
    /// row to join the pool starts playback.
    /// </para>
    /// </summary>
    internal async Task<PlaybackResult?> RunPlaybackChainAsync(
        Game game,
        EnrichedStream first,
        IPlaybackLauncher launcher,
        Action onPoolEmpty,
        object playbackLock,
        CancellationToken cancellationToken)
    {
        var stream = first;
        while (true)
        {
            var attempt = new PlayAttempt();
            var result = await PlayStreamAsync(game, stream, launcher, cancellationToken, attempt).ConfigureAwait(false);
            if (result is null || result.Success || result.ViewerLeft || cancellationToken.IsCancellationRequested)
                return result;

            if (result.IsLocalServiceUnavailable)
            {
                // Infrastructure, not the stream: no report, nothing removed. A row that
                // joins the pool later (the service is back) starts playback again.
                lock (playbackLock)
                    onPoolEmpty();
                return result;
            }

            // The player never opened: the row we tried is the one that failed. Once it
            // opened, the native session may have moved through the pool, so the row
            // under the pool index is the one it ended on.
            var failed = attempt.PlayerOpened ? streamSwitchingService.GetCurrentStream() : stream;
            if (failed != null)
            {
                _ = ReportHealthAsync(game, failed.Stream, "failed", result.Message, failed.Health, cancellationToken);
                RemoveFromPool(failed);
            }

            EnrichedStream? next;
            lock (playbackLock)
            {
                // RemoveCurrentStream leaves the index on the row that took the failed
                // row's place (a merged fallback, or the following row). Play that one:
                // the old TryNextHealthyStreamAsync took GetNextHealthyStream() here,
                // which skipped a row, left the pool index on a row that was not playing,
                // and dropped the result of the next play.
                next = streamSwitchingService.GetCurrentStream();
                if (next == null)
                    onPoolEmpty();
            }

            _healthyStreamCount = streamSwitchingService.GetHealthyStreams().Count;
            if (next == null)
            {
                _status = "All available streams failed - testing others...";
                PublishProgress();
                return result;
            }

            logger.LogInformation(
                "[StreamResolution] Start failed on {Failed} ({Message}) - playing next healthy {Next}",
                failed?.Stream?.Channel, result.Message, next.Stream?.Channel);
            PublishProgress();
            stream = next;
        }
    }

    private void RemoveFromPool(EnrichedStream failed)
    {
        if (!ReferenceEquals(streamSwitchingService.GetCurrentStream(), failed))
        {
            var healthy = streamSwitchingService.GetHealthyStreams();
            var index = -1;
            for (var i = 0; i < healthy.Count; i++)
            {
                if (ReferenceEquals(healthy[i], failed))
                {
                    index = i;
                    break;
                }
            }

            // Already gone (the native session removed it): nothing to do.
            if (index < 0 || !streamSwitchingService.SwitchToStream(index))
                return;
        }

        streamSwitchingService.RemoveCurrentStream();
    }

    /// <summary>Whether a <see cref="PlayStreamAsync"/> call got as far as opening the player.</summary>
    internal sealed class PlayAttempt
    {
        public bool PlayerOpened { get; set; }
    }

    private PlaybackResult LocalServiceUnavailableResult(EnrichedStream enrichedStream, InfrastructureUnavailableException ex)
    {
        logger.LogWarning(
            "[StreamResolution] Fresh resolve for {Channel} deferred - local service unavailable: {Message}",
            enrichedStream.Stream?.Channel, ex.Message);
        return PlaybackResult.Completed($"{PlaybackCloseReasons.LocalServiceUnavailable}: {ex.Message}", true);
    }



    private void PublishProgress()
    {
        _progressSubject.OnNext(new StreamResolutionProgress
        {
            IsResolving = _isResolving,
            Status = _status,
            TotalStreams = _totalStreams,
            StreamsTested = _streamsTested,
            HealthyStreams = _healthyStreamCount
        });
    }

    /// <summary>
    /// Token of the session currently inside the start gate. A replacement
    /// start reads it to tell a cancelled unwind from a live overlap.
    /// </summary>
    private sealed class ActiveSession(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
    }

    /// <summary>
    /// Starts <see cref="IRemoteComputeKeepAlive"/> for one relayed resolve
    /// session and stops it when <see cref="StartCoreAsync"/> returns.
    /// </summary>
    private sealed class RelayKeepAliveHold : IDisposable
    {
        private readonly IRemoteComputeKeepAlive? _keepAlive;

        private RelayKeepAliveHold(IRemoteComputeKeepAlive? keepAlive) => _keepAlive = keepAlive;

        public static RelayKeepAliveHold Start(IRemoteComputeKeepAlive? keepAlive, bool hold)
        {
            if (!hold)
            {
                return new RelayKeepAliveHold(null);
            }

            keepAlive?.Start();
            return new RelayKeepAliveHold(keepAlive);
        }

        public void Dispose() => _keepAlive?.Stop();
    }
}
