using Microsoft.Extensions.Logging;
using System.Threading;
using VardyParty.Hosting;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Presentation;
using VardyParty.Streaming;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;

namespace VardyParty.Platforms.Windows
{
    public partial class WindowsVideoPlayerService
    {
        private sealed partial class PlayerSession
        {
            private void ReleasePreviousPlaybackResources()
            {
                if (activeAdaptiveMediaSource != null && activeDownloadHandler != null)
                {
                    try { activeAdaptiveMediaSource.DownloadRequested -= activeDownloadHandler; } catch { }
                }

                activeAdaptiveMediaSource = null;
                activeDownloadHandler = null;
                CancelPlaybackDownloads();
                DisposeRootedPlaybackStreams();
            }

            private CancellationToken CurrentPlaybackDownloadToken
            {
                get
                {
                    lock (playbackDownloadGate)
                        return playbackDownloadsCts.Token;
                }
            }

            private void CancelPlaybackDownloads()
            {
                lock (playbackDownloadGate)
                {
                    retiredPlaybackDownloads.Add(playbackDownloadsCts);
                    try { playbackDownloadsCts.Cancel(); } catch { }
                    playbackDownloadsCts = new CancellationTokenSource();
                }
            }

            private void DisposePlaybackDownloadSources()
            {
                List<CancellationTokenSource> sources;
                lock (playbackDownloadGate)
                {
                    sources = retiredPlaybackDownloads.ToList();
                    retiredPlaybackDownloads.Clear();
                    sources.Add(playbackDownloadsCts);
                    playbackDownloadsCts = new CancellationTokenSource();
                }

                foreach (var source in sources)
                {
                    try { source.Cancel(); } catch { }
                    try { source.Dispose(); } catch { }
                }
            }

            /// <summary>
            /// Keep <paramref name="stream"/> alive for Media Foundation. Returns false
            /// when this attach is already over; the stream is disposed in that case.
            /// </summary>
            private bool TryRootPlaybackStream(IDisposable stream, int generation)
            {
                lock (rootedPlaybackStreamsGate)
                {
                    if (IsStaleAttach(generation))
                    {
                        try { stream.Dispose(); } catch { }
                        return false;
                    }

                    rootedPlaybackStreams.Add(stream);
                    return true;
                }
            }

            private void DisposeRootedPlaybackStreams()
            {
                List<IDisposable> rooted;
                lock (rootedPlaybackStreamsGate)
                {
                    rooted = rootedPlaybackStreams.ToList();
                    rootedPlaybackStreams.Clear();
                }

                foreach (var stream in rooted)
                {
                    try { stream.Dispose(); } catch { }
                }
            }

            private void CleanupMediaPlayer()
            {
                if (cleanupInvoked) return;
                cleanupInvoked = true;
                StopVideoProbe();
                StopHealthMetricsLoop();
                CancelPlaybackDownloads();
                _host.UnbindTransferMeter(transferMeter);
                transferMeter.Reset();

                try
                {
                    try { healthyStreamsSubscription?.Dispose(); } catch { }
                    try { currentIndexSubscription?.Dispose(); } catch { }
                    try { gamesSubscription?.Dispose(); } catch { }
                    try { streamInfoHideTimer?.Stop(); } catch { }
                    StopTickerScroll();
                    if (naturalVideoSizeChangedHandler != null)
                        mediaPlayer.PlaybackSession.NaturalVideoSizeChanged -= naturalVideoSizeChangedHandler;
                    if (playbackStateChangedHandler != null)
                        mediaPlayer.PlaybackSession.PlaybackStateChanged -= playbackStateChangedHandler;
                    if (positionChangedHandler != null)
                        mediaPlayer.PlaybackSession.PositionChanged -= positionChangedHandler;
                    if (mediaEndedHandler != null)
                        mediaPlayer.MediaEnded -= mediaEndedHandler;
                    if (mediaFailedHandler != null)
                        mediaPlayer.MediaFailed -= mediaFailedHandler;
                }
                catch { }

                ReleasePreviousPlaybackResources();

                try
                {
                    mediaPlayer.Pause();
                    mediaPlayer.Source = null;
                    _host._currentPlaybackItem = null;
                }
                catch { }

                try
                {
                    mediaPlayerElement.SetMediaPlayer(null);
                }
                catch { }

                try
                {
                    mediaPlayer.Dispose();
                }
                catch { }

                // Dispose the switch lock last so in-flight StartPlaybackAsync can exit Wait/Release safely.
                DisposePlaybackDownloadSources();
                try { playbackSwitchLock.Dispose(); } catch { }
            }

            private void Restore()
            {
                _host._logger.LogInformation("Restore: closing native player");
                // Unhook synchronously so the very next X-press is never cancelled
                try
                {
                    if (nativeWindow?.AppWindow != null && appWindowClosingHandler != null)
                    {
                        nativeWindow.AppWindow.Closing -= appWindowClosingHandler;
                        appWindowClosingHandler = null;
                    }
                }
                catch { }

                void DoRestore()
                {
                    StopTickerScroll();
                    try { scoresTickerTrack.Children.Clear(); } catch { }
                    CleanupMediaPlayer();
                    HidePlayerOverlay();
                    WindowsWindowChrome.ApplyMainWindowChrome(nativeWindow);
                    isClosingPlayer = false;
                }

                var queue = nativeWindow?.DispatcherQueue;
                if (queue != null && queue.HasThreadAccess)
                {
                    DoRestore();
                }
                else
                {
                    MainThread.BeginInvokeOnMainThread(DoRestore);
                }
            }

            private void ClosePlayerSession(string message)
            {
                if (cleanupInvoked) return;
                switchBack?.Stop();
                Restore();
                _tcs.TrySetResult(PlaybackResult.Completed(message, true));
            }

            private void SyncHealthyStreamCount()
            {
                session.SetHealthyStreamCount(switchingService?.GetHealthyStreams().Count ?? 0);
            }

            private void DispatchEngine(MediaEngineEvent engineEvent)
            {
                try
                {
                    SyncHealthyStreamCount();
                    ApplyPlaybackCommand(PlaybackCommand.FromEffects(session.Handle(engineEvent)));
                }
                catch (Exception ex)
                {
                    _host._logger.LogError(ex, "DispatchEngine failed ({Kind})", engineEvent.Kind);
                }
            }

            private void AttachViaSession(string url, bool usedCachedUrl = false, bool force = false)
            {
                // Intentional attach (switch / start) — reset soft live recoveries.
                liveHlsRecoveries = 0;
                liveHlsSoftRecoverInFlight = false;
                SyncHealthyStreamCount();
                ApplyPlaybackCommand(PlaybackCommand.FromEffects(session.BeginAttach(url, usedCachedUrl, force)));
            }

            /// <summary>
            /// Linux LibVLC reconnects; Android seeks to live edge on BehindLiveWindow.
            /// WinUI has no BLWE API — rebuild AdaptiveMediaSource for the same URL instead of
            /// raising Error (which removes the stream from the pool).
            /// </summary>
            private bool TryRecoverLiveHlsFailure(MediaPlayerFailedEventArgs? args)
            {
                if (!IsRecoverableMediaFailure(args))
                    return false;

                // Nested MediaFailed while soft-reattach is already queued — absorb without stacking.
                if (liveHlsSoftRecoverInFlight)
                {
                    _host._logger.LogDebug(
                        "Live HLS MediaFailed while soft-recover in flight — coalescing");
                    return true;
                }

                if (!PlaybackPolicy.ShouldAttemptLiveHlsRecovery(liveHlsRecoveries, currentPlaybackUrl))
                {
                    _host._logger.LogWarning(
                        "Live HLS recoveries exhausted ({Count}) — escalating MediaFailed",
                        liveHlsRecoveries);
                    return false;
                }

                var url = currentPlaybackUrl;
                liveHlsRecoveries++;
                liveHlsSoftRecoverInFlight = true;
                MainThread.BeginInvokeOnMainThread(SyncChromeContentStateFromPlayback);
                var errMsg = args?.ErrorMessage ?? "unknown";
                _host._logger.LogWarning(
                    "Live HLS MediaFailed — reattaching to live edge (attempt {Attempt}/{Max}): {Error}",
                    liveHlsRecoveries,
                    PlaybackPolicy.MaxLiveHlsRecoveries,
                    errMsg);

                try
                {
                    engine.Raise(MediaEngineEvent.Buffering(session.Snapshot.AttachGeneration, true));
                }
                catch (Exception ex)
                {
                    _host._logger.LogDebug(ex, "Buffering raise during live recover failed");
                }

                // Do not BeginAttach — keep session generation / pool entry; only rebuild the OS source.
                _ = SoftRecoverPlaybackAsync(url);
                return true;
            }

            /// <summary>
            /// Soft live reattach: clears <see cref="liveHlsSoftRecoverInFlight"/> when finished
            /// so a later NetworkError can start another budgeted attempt.
            /// </summary>
            private async Task SoftRecoverPlaybackAsync(string url)
            {
                try
                {
                    await StartPlaybackAsync(url).ConfigureAwait(false);
                }
                finally
                {
                    liveHlsSoftRecoverInFlight = false;
                    MainThread.BeginInvokeOnMainThread(SyncChromeContentStateFromPlayback);
                }
            }

            private static bool IsRecoverableMediaFailure(MediaPlayerFailedEventArgs? args)
            {
                if (args == null)
                {
                    return PlaybackPolicy.IsRecoverableLiveHlsMediaFailure(
                        isNetworkError: true,
                        isDecodingError: false,
                        isUnknownError: false,
                        isSourceNotSupported: false,
                        isAborted: false);
                }

                var detail = $"{args.ErrorMessage} {args.ExtendedErrorCode?.Message}";
                return PlaybackPolicy.IsRecoverableLiveHlsMediaFailure(
                    isNetworkError: args.Error == MediaPlayerError.NetworkError,
                    isDecodingError: args.Error == MediaPlayerError.DecodingError,
                    isUnknownError: args.Error == MediaPlayerError.Unknown,
                    isSourceNotSupported: args.Error == MediaPlayerError.SourceNotSupported,
                    isAborted: args.Error == MediaPlayerError.Aborted,
                    detailMessage: detail);
            }

            /// <summary>
            /// How long an attach may go without reaching <c>Playing</c> before it
            /// counts as a failed start. This is an AMS engine failsafe, not the
            /// shared no-picture check: <see cref="PlaybackPolicy.NoVideoTimeout"/>
            /// (8s) starts once Ready/Playing is raised. AMS can take longer than
            /// 8s to reach Playing, so this remains 20s.
            /// </summary>
            private static readonly TimeSpan StartWithoutPictureTimeout = TimeSpan.FromSeconds(20);

            /// <summary>
            /// Ready means "the picture is playing": PlaybackSession reached Playing. It used to be
            /// raised as soon as the MediaPlaybackItem was assigned, before a single segment, so a
            /// stream whose segments were all 403 counted as established and working (last good,
            /// ReportWorking, and three same-stream retries instead of one fresh resolve and Next;
            /// Windows log 13 Nov 2026 19:55:26-31).
            /// </summary>
            private void ArmReadyOnPlaying()
            {
                var generation = session.Snapshot.AttachGeneration;
                Interlocked.Exchange(ref readyPendingGeneration, generation);
                lastDownloadFailureStatus = 0;
                CancelReadyWatchdog();
                var cts = new CancellationTokenSource();
                readyWatchdogCts = cts;
                _ = WatchStartAsync(generation, cts.Token);
            }

            private async Task WatchStartAsync(long generation, CancellationToken cancellationToken)
            {
                try
                {
                    await Task.Delay(StartWithoutPictureTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref readyPendingGeneration, -1, generation) != generation
                    || cleanupInvoked)
                    return;
                _host._logger.LogWarning(
                    "No picture {Seconds}s after attach (generation {Generation}) - failing the start",
                    (int)StartWithoutPictureTimeout.TotalSeconds,
                    generation);
                var refused = lastDownloadFailureStatus;
                engine.Raise(MediaEngineEvent.Error(
                    generation,
                    PlaybackPolicy.IsRefusedStatus(refused)
                        ? PlaybackPolicy.RefusedDownloadReason(refused)
                        : $"Start timed out: no picture after {(int)StartWithoutPictureTimeout.TotalSeconds}s"));
            }

            private void RaiseReadyIfPending()
            {
                var generation = Interlocked.Exchange(ref readyPendingGeneration, -1);
                if (generation < 0 || generation != session.Snapshot.AttachGeneration)
                    return;
                CancelReadyWatchdog();
                engine.Raise(MediaEngineEvent.Ready(generation));
                ArmVideoProbe();
                ProbeVideo();
            }

            private (string? Url, string? Name) CurrentHealthIdentity()
                => StreamHealthIdentity.ResolvePlaybackReportIdentity(
                    switchingService?.GetCurrentStream(),
                    session.Snapshot.CurrentUrl,
                    _refererUrl);

            private void PostHealthError(string? error)
            {
                try
                {
                    var (url, name) = CurrentHealthIdentity();
                    _ = _host._healthReporter.ReportPlaybackErrorAsync(url, _refererUrl, name, error: error);
                }
                catch (Exception ex) { _host.LogIgnored("ReportPlaybackError", ex); }
            }

            private void PostHealthBuffering()
            {
                try
                {
                    var metrics = _host.GetCurrentMetrics();
                    if (metrics != null)
                        metrics.IsBuffering = true;
                    var (url, name) = CurrentHealthIdentity();
                    _ = _host._healthReporter.ReportBufferingAsync(url, _refererUrl, name, metrics: metrics);
                }
                catch (Exception ex) { _host.LogIgnored("ReportBuffering", ex); }
            }

            private void StartHealthMetricsLoop()
            {
                StopHealthMetricsLoop();
                healthMetricsTimer = new Timer(_ =>
                {
                    try
                    {
                        var queue = nativeWindow?.DispatcherQueue;
                        if (queue != null && !queue.HasThreadAccess)
                        {
                            queue.TryEnqueue(PostHealthMetrics);
                            return;
                        }
                        PostHealthMetrics();
                    }
                    catch (Exception ex) { _host.LogIgnored("HealthMetricsTimer", ex); }
                }, null, PlaybackPolicy.HealthMetricsInterval, PlaybackPolicy.HealthMetricsInterval);
            }

            private void StopHealthMetricsLoop()
            {
                var timer = Interlocked.Exchange(ref healthMetricsTimer, null);
                try { timer?.Dispose(); } catch (Exception ex) { _host.LogIgnored("StopHealthMetricsLoop", ex); }
            }

            private void PostHealthMetrics()
            {
                if (cleanupInvoked)
                    return;
                var metrics = _host.GetCurrentMetrics();
                var (url, name) = CurrentHealthIdentity();
                _ = _host._healthReporter.ReportPlaybackMetricsAsync(url, _refererUrl, name, metrics: metrics);
                var buffering = false;
                try { buffering = mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Buffering; }
                catch { }
                engine.Raise(MediaEngineEvent.Metrics(session.Snapshot.AttachGeneration, metrics?.BitrateKbps, buffering));
            }

            /// <summary>
            /// Tells the session whether this attach has shown video. A natural
            /// size confirms the picture immediately; the overlay still waits
            /// for <see cref="DecodedPictureSizeSettler"/> to settle.
            /// </summary>
            private void ProbeVideo()
            {
                try
                {
                    if (cleanupInvoked || session.Snapshot.IsVideoConfirmed)
                    {
                        StopVideoProbe();
                        return;
                    }

                    var width = 0;
                    var height = 0;
                    try
                    {
                        width = (int)mediaPlayer.PlaybackSession.NaturalVideoWidth;
                        height = (int)mediaPlayer.PlaybackSession.NaturalVideoHeight;
                    }
                    catch { }

                    engine.Raise(MediaEngineEvent.VideoProbe(
                        session.Snapshot.AttachGeneration,
                        width > 0 && height > 0));
                }
                catch (Exception ex) { _host.LogIgnored("ProbeVideo", ex); }
            }

            private void ArmVideoProbe()
            {
                try
                {
                    if (videoProbeTimer is null)
                    {
                        videoProbeTimer = new Microsoft.UI.Xaml.DispatcherTimer
                        {
                            Interval = TimeSpan.FromSeconds(1)
                        };
                        videoProbeTimer.Tick += (_, _) => ProbeVideo();
                    }
                    videoProbeTimer.Start();
                }
                catch (Exception ex) { _host.LogIgnored("ArmVideoProbe", ex); }
            }

            private void StopVideoProbe()
            {
                try { videoProbeTimer?.Stop(); }
                catch (Exception ex) { _host.LogIgnored("StopVideoProbe", ex); }
            }

            private void CancelReadyWatchdog()
            {
                var cts = Interlocked.Exchange(ref readyWatchdogCts, null);
                if (cts is null)
                    return;
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }

            /// <summary>
            /// Once per attached URL: which headers the segment requests carry (names, plus the
            /// Referer/Origin values; never cookie values), so a 403 can be matched to them in the log.
            /// </summary>
            private void LogSegmentHeadersOnce(Uri segmentUri, string referer, IReadOnlyDictionary<string, string>? requestHeaders)
            {
                var key = currentPlaybackUrl ?? segmentUri.Host;
                if (string.Equals(Volatile.Read(ref segmentHeadersLoggedFor), key, StringComparison.Ordinal))
                    return;
                Volatile.Write(ref segmentHeadersLoggedFor, key);
                var headers = PlaybackRequestHeaders.Resolve(referer, requestHeaders);
                string? Value(string name) => headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
                _host._logger.LogInformation(
                    "Segment headers for {Host}: Referer={Referer} Origin={Origin} names=[{Names}]",
                    segmentUri.Host,
                    Value("Referer") ?? "(none)",
                    Value("Origin") ?? "(none)",
                    string.Join(",", headers.Select(h => h.Key)));
            }

            private SwitchBackRunner CreateSwitchBackRunner()
                => new(
                    session,
                    () => SwitchBackCommands.FromRow(switchingService.GetCurrentStream(), null),
                    SwitchBackPlaylistProbe.GetAsync,
                    (candidate, reason, _) =>
                    {
                        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            try
                            {
                                if (!SwitchBackPlaylistProbe.RestoreAndSwitchTo(switchingService, candidate))
                                    _host._logger.LogInformation("[switch-back] abandoned row not in pool ({Reason})", reason);
                                tcs.TrySetResult(true);
                            }
                            catch (Exception ex)
                            {
                                tcs.TrySetException(ex);
                            }
                        });
                        return tcs.Task;
                    },
                    msg => _host._logger.LogInformation("{Message}", msg));

            private void ApplyPlaybackCommand(PlaybackCommand cmd)
            {
                PlaybackCommandExecutor.Apply(cmd, new WindowsPlaybackCommandHost(this));
            }

            private sealed class WindowsPlaybackCommandHost(PlayerSession session) : IPlaybackCommandHost
            {
                public void BeginIndexSwitchSuppression() => session.suppressIndexDrivenSwitch = true;

                public void EndIndexSwitchSuppression() => session.suppressIndexDrivenSwitch = false;

                public void ClearCurrentResolvedUrl() => session.pool.ClearCurrentResolvedUrl();

                public void RemoveCurrentFromPool() => session.pool.RemoveCurrentFromPool();

                public void SyncHealthyStreamCount() => session.pool.SyncHealthyStreamCount();

                public void ReportFailed(string? reason)
                {
                    session._host._logger.LogInformation("Stream failed: {Reason}", reason ?? "Playback error");
                    session.PostHealthError(reason);
                }

                public void ReportDeclined(string? reason)
                {
                    session._host._logger.LogInformation("Stream declined: {Reason}", reason ?? "Health declined");
                    session.PostHealthError(reason);
                }

                /// <summary>Short, accurate text (never the raw Media Foundation message).</summary>
                public void ShowFailureToUser(string userText) => session.ShowStreamError(userText);

                public void ReportWorking()
                {
                    // Catalog page URL of the pool row, the same key the resolver reports.
                    var (url, streamName) = StreamHealthIdentity.ResolvePlaybackReportIdentity(
                        session.switchingService?.GetCurrentStream(),
                        session.session.Snapshot.CurrentUrl,
                        session._refererUrl);
                    _ = session._host._healthReporter.ReportPlaybackStartedAsync(
                        url,
                        session._refererUrl,
                        streamName,
                        metrics: session._host.GetCurrentMetrics());
                    session.StartHealthMetricsLoop();
                }

                public void MarkEstablished()
                {
                    // Session established flag is owned by PlaybackSessionController.Handle(Ready).
                }

                public void RaiseBuffering(bool isBuffering)
                {
                    session._host.BufferingStateChanged?.Invoke(session._host, isBuffering);
                    if (isBuffering)
                        session.PostHealthBuffering();
                }

                public void Attach(string url, bool isRevert)
                {
                    if (isRevert)
                        session._host._logger.LogWarning("Reverting to last good stream: {Url}", url);
                    // Each row's CDN checks its own Referer: send the attached row's, never the
                    // opening stream's (403 on Next, Windows log 13 Nov 2026 19:41:33).
                    session.ApplyRequestContextForUrl(url);
                    _ = session.engine.AttachAsync(url, session._requestHeaders);
                }

                public void RevertPoolIndexTo(string url) => session.pool.RevertIndexToPlayingUrl(url);

                public void AttachCurrentAfterRemove() => _ = session.pool.AttachCurrentFromPoolAsync();

                public void RetryFreshResolve() => _ = session.pool.RetryFreshResolveAsync();

                public void StopEngine()
                {
                    try
                    {
                        session.mediaPlayer.Pause();
                        session.mediaPlayer.Source = null;
                    }
                    catch (Exception ex)
                    {
                        session._host._logger.LogWarning(ex, "Stop engine failed");
                    }
                }

                public void CloseSession(string reason) => session.ClosePlayerSession(reason);

                public void SwitchPoolToNext()
                {
                    if (session._onNextStreamRequested != null && !session.isNextStreamRequestInProgress)
                    {
                        session.isNextStreamRequestInProgress = true;
                        _ = session.InvokeNextStreamAsync();
                    }
                }

                public void SwitchPoolToPrevious() => session.pool.SwitchPoolToPrevious();

                public void NotifyApplyFailed(Exception exception)
                    => session._host._logger.LogError(exception, "ApplyPlaybackCommand failed");

                public void ConsiderSwitchBack(PlaybackCommand cmd)
                    => session.switchBack?.Consider(cmd);
            }

            private async Task InvokeNextStreamAsync()
            {
                try
                {
                    if (_onNextStreamRequested != null)
                        await _onNextStreamRequested();
                }
                catch (Exception ex)
                {
                    _host._logger.LogError(ex, "Auto-advance after session command failed");
                }
                finally
                {
                    isNextStreamRequestInProgress = false;
                }
            }

            private void PreparePlaybackSwitchOnUiThread(int generation)
            {
                if (IsStaleAttach(generation)) return;

                StopTickerScroll();
                ReleasePreviousPlaybackResources();
                transferMeter.Reset();
                try
                {
                    mediaPlayer.Pause();
                    mediaPlayer.Source = null;
                    _host._currentPlaybackItem = null;
                }
                catch { }
            }

            private void ApplyRequestContextForUrl(string url)
            {
                var context = PlaybackRowRequestContext.ForUrl(
                    switchingService,
                    url,
                    new PlaybackRequestContext(_refererUrl, _requestHeaders));
                if (!string.Equals(context.Referer, _refererUrl, StringComparison.Ordinal))
                {
                    _host._logger.LogInformation(
                        "Playback Referer for {Host}: {Referer}",
                        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "(relative)",
                        context.Referer);
                }

                _refererUrl = context.Referer;
                _requestHeaders = context.Headers;
            }

            private bool IsStaleAttach(int generation) =>
                cleanupInvoked || generation != (int)session.Snapshot.AttachGeneration;

            private async Task StartPlaybackAsync(string url)
            {
                if (cleanupInvoked) return;

                try
                {
                    await playbackSwitchLock.WaitAsync();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                var generation = (int)session.Snapshot.AttachGeneration;

                try
                {
                    if (IsStaleAttach(generation))
                        return;

                    // Manifest and every later segment go through the DoH-aware managed
                    // client. WinRT HttpClient ignores that handler, so a host the
                    // ISP cannot resolve never starts.
                    var managedHttp = _host._httpClientFactory.CreateClient(PlaybackHttpClients.Media);
                    var uri = new Uri(url);
                    var manifestToken = CurrentPlaybackDownloadToken;
                    // This attach's headers: a later switch must not change an older source's.
                    var referer = _refererUrl;
                    var requestHeaders = _requestHeaders;
                    var created = await _host.CreateAdaptiveMediaSourceAsync(
                        managedHttp,
                        uri,
                        referer,
                        requestHeaders,
                        manifestToken);
                    var adaptiveResult = created.Result;
                    var manifestStream = created.ManifestStream;

                    try
                    {
                        if (IsStaleAttach(generation))
                            return;

                        if (adaptiveResult.Status != AdaptiveMediaSourceCreationStatus.Success
                            || adaptiveResult.MediaSource == null
                            || manifestStream == null)
                        {
                            throw new InvalidOperationException($"Adaptive source failed: {adaptiveResult.Status}");
                        }

                        await MainThread.InvokeOnMainThreadAsync(() => PreparePlaybackSwitchOnUiThread(generation));
                        if (IsStaleAttach(generation))
                            return;

                        if (!TryRootPlaybackStream(manifestStream, generation))
                        {
                            manifestStream = null;
                            return;
                        }

                        manifestStream = null;

                        var segmentToken = CurrentPlaybackDownloadToken;
                        // AMS already loaded the first playlist; DownloadRequested playlist
                        // fetches are mid-play. If the next retry would exceed the deferral
                        // budget, give up as CDN-unreachable and switch — never serve a
                        // stale last-good playlist (already-watched segments).
                        var playlistReload = new PlaylistReloadSession(
                            _host._services.GetService(typeof(IPlaylistReloadRecoveryStrategy)) as IPlaylistReloadRecoveryStrategy,
                            PlaylistReloadRowName.ForUrl(switchingService, url))
                        {
                            FirstPlaylistCompleted = true
                        };
                        playlistReloadSession = playlistReload;
                        TypedEventHandler<AdaptiveMediaSource, AdaptiveMediaSourceDownloadRequestedEventArgs> downloadHandler = async (sender, args) =>
                        {
                            if (IsStaleAttach(generation))
                                return;

                            var deferral = args.GetDeferral();
                            System.Net.Http.HttpRequestMessage? managedRequest = null;
                            System.Net.Http.HttpResponseMessage? managedResponse = null;
                            try
                            {
                                if (IsStaleAttach(generation))
                                    return;

                                var resourceUrl = args.ResourceUri.ToString();
                                var isPlaylist = args.ResourceType == AdaptiveMediaSourceResourceType.Manifest
                                    || PlaylistReloadEvaluation.IsPlaylistUrl(resourceUrl);
                                if (isPlaylist)
                                {
                                    var outcome = await PlaylistReloadCoordinator.RunAsync(
                                        playlistReload.Strategy,
                                        PlaylistReloadRequest.MidPlay(
                                            isFirstAttach: !playlistReload.FirstPlaylistCompleted,
                                            playlistReload,
                                            playlistReload.Row,
                                            log: (isInfo, line) =>
                                            {
                                                if (isInfo)
                                                    _host._logger.LogInformation("{Line}", line);
                                                else
                                                    _host._logger.LogDebug("{Line}", line);
                                            }),
                                        async ct =>
                                        {
                                            using var req = new System.Net.Http.HttpRequestMessage(
                                                System.Net.Http.HttpMethod.Get, resourceUrl);
                                            ApplyManagedPlaybackHeaders(req, referer, requestHeaders);
                                            using var resp = await managedHttp.SendAsync(
                                                req,
                                                System.Net.Http.HttpCompletionOption.ResponseHeadersRead,
                                                ct).ConfigureAwait(false);
                                            var bodyBytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                                            return PlaylistReloadFetchResult.FromHttp(
                                                (int)resp.StatusCode,
                                                bodyBytes,
                                                resp.Content.Headers.ContentType?.MediaType);
                                        },
                                        segmentToken).ConfigureAwait(false);

                                    if (outcome.Kind is PlaylistReloadOutcomeKind.Serve)
                                    {
                                        var playlistBody = outcome.Body ?? Array.Empty<byte>();
                                        var playlistType = PlaybackContentTypes.Resolve(
                                            outcome.ContentType,
                                            args.ResourceUri.AbsolutePath,
                                            ToPlaybackResourceKind(args.ResourceType));
                                        transferMeter.Add(playlistBody.Length, Environment.TickCount64);
                                        var playlistStream = await CopyToRandomAccessStreamAsync(playlistBody);
                                        if (IsStaleAttach(generation) || segmentToken.IsCancellationRequested)
                                        {
                                            playlistStream.Dispose();
                                            return;
                                        }

                                        if (!TryRootPlaybackStream(playlistStream, generation))
                                            return;

                                        args.Result.InputStream = playlistStream.GetInputStreamAt(0);
                                        args.Result.ContentType = playlistType;
                                        if (outcome.Kind == PlaylistReloadOutcomeKind.Serve)
                                            playlistReload.NoteServed(playlistBody);
                                        lastDownloadFailureStatus = 0;
                                        session.NotifyDownloadSuccess();
                                        return;
                                    }

                                    throw new System.Net.Http.HttpRequestException(
                                        outcome.Message ?? $"HTTP {outcome.StatusCode} playlist reload failed",
                                        inner: null,
                                        statusCode: outcome.StatusCode is >= 100 and <= 599
                                            ? (System.Net.HttpStatusCode)outcome.StatusCode
                                            : null);
                                }

                                managedRequest = new System.Net.Http.HttpRequestMessage(
                                    System.Net.Http.HttpMethod.Get,
                                    args.ResourceUri.ToString());
                                ApplyManagedPlaybackHeaders(managedRequest, referer, requestHeaders);
                                ApplyResourceByteRange(managedRequest, args);
                                if (args.ResourceType == AdaptiveMediaSourceResourceType.MediaSegment && lastDownloadFailureStatus == 0)
                                    LogSegmentHeadersOnce(args.ResourceUri, referer, requestHeaders);

                                managedResponse = await managedHttp.SendAsync(
                                    managedRequest,
                                    System.Net.Http.HttpCompletionOption.ResponseHeadersRead,
                                    segmentToken);
                                managedResponse.EnsureSuccessStatusCode();

                                if (IsStaleAttach(generation) || segmentToken.IsCancellationRequested)
                                    return;

                                var contentType = PlaybackContentTypes.Resolve(
                                    managedResponse.Content.Headers.ContentType?.MediaType,
                                    args.ResourceUri.AbsolutePath,
                                    ToPlaybackResourceKind(args.ResourceType));
                                var body = await managedResponse.Content.ReadAsByteArrayAsync(segmentToken);
                                // TikTok-CDN segments can be MPEG-TS wrapped in a PNG (TIKTIKPX):
                                // Media Foundation would reject image bytes. Only whole segments
                                // (no byte range) can be unwrapped. Shared rule: VardyParty.Streaming.SegmentPayload.
                                if (args.ResourceType == AdaptiveMediaSourceResourceType.MediaSegment
                                    && managedRequest.Headers.Range is null)
                                {
                                    body = SegmentPayload.NormalizeForPlayback(body, out var unwrapped);
                                    if (unwrapped)
                                    {
                                        contentType = SegmentPayload.MpegTsContentType;
                                        _host._logger.LogInformation(
                                            "Unwrapped image-wrapped TS segment ({Bytes} bytes) {Uri}",
                                            body.Length,
                                            args.ResourceUri);
                                    }
                                }

                                transferMeter.Add(
                                    body.Length,
                                    Environment.TickCount64);

                                var winrtStream = await CopyToRandomAccessStreamAsync(body);
                                if (IsStaleAttach(generation) || segmentToken.IsCancellationRequested)
                                {
                                    winrtStream.Dispose();
                                    return;
                                }

                                if (!TryRootPlaybackStream(winrtStream, generation))
                                    return;

                                args.Result.InputStream = winrtStream.GetInputStreamAt(0);
                                args.Result.ContentType = contentType;
                                lastDownloadFailureStatus = 0;
                                session.NotifyDownloadSuccess();
                            }
                            catch (OperationCanceledException) when (IsStaleAttach(generation) || segmentToken.IsCancellationRequested)
                            {
                            }
                            catch (ObjectDisposedException) when (cleanupInvoked || segmentToken.IsCancellationRequested)
                            {
                            }
                            catch (Exception ex)
                            {
                                var statusCode = StatusCodeFromDownloadException(ex);

                                _host._logger.LogWarning(
                                    ex,
                                    "Segment download failed ({ResourceType}, status={StatusCode}, uri={Uri})",
                                    args.ResourceType,
                                    statusCode,
                                    args.ResourceUri);

                                lastDownloadFailureStatus = statusCode;
                                var failureMessage =
                                    $"Segment download failed ({args.ResourceType}, status={statusCode})";
                                var downloadCmd = PlaybackCommand.FromEffects(
                                    session.NotifyDownloadFailure(failureMessage, statusCode));
                                if (!downloadCmd.IsNoOp)
                                {
                                    MainThread.BeginInvokeOnMainThread(() =>
                                    {
                                        try
                                        {
                                            if (IsStaleAttach(generation))
                                                return;
                                            ApplyPlaybackCommand(downloadCmd);
                                        }
                                        catch (Exception marshalEx)
                                        {
                                            _host._logger.LogWarning(marshalEx, "Download-failure command failed");
                                        }
                                    });
                                }

                                try
                                {
                                    // A failure HRESULT (HTTP_E_STATUS_*), not the bare HTTP status:
                                    // WinRT threw "The parameter is incorrect" for 403 on 13 Nov.
                                    args.Result.ExtendedStatus = PlaybackPolicy.WindowsDownloadFailureStatus(statusCode);
                                }
                                catch (Exception statusEx)
                                {
                                    _host._logger.LogDebug(statusEx, "Failed to set download extended status");
                                }
                            }
                            finally
                            {
                                managedResponse?.Dispose();
                                managedRequest?.Dispose();
                                deferral.Complete();
                            }
                        };

                        activeAdaptiveMediaSource = adaptiveResult.MediaSource;
                        activeDownloadHandler = downloadHandler;
                        adaptiveResult.MediaSource.DownloadRequested += downloadHandler;

                        var mediaSource = MediaSource.CreateFromAdaptiveMediaSource(adaptiveResult.MediaSource);
                        var playbackItem = new MediaPlaybackItem(mediaSource);

                        if (IsStaleAttach(generation))
                            return;

                        // Ensure UI updates happen on the main thread
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            try
                            {
                                if (IsStaleAttach(generation))
                                    return;

                                ArmReadyOnPlaying();
                                mediaPlayer.Source = playbackItem;
                                _host._currentPlaybackItem = playbackItem;

                                // Add mediaPlayerElement to grid now that source is set
                                if (playerGrid is { } overlayGrid && !overlayGrid.Children.Contains(mediaPlayerElement))
                                {
                                    overlayGrid.Children.Insert(0, mediaPlayerElement); // Insert at index 0 to be behind other elements
                                }

                                // Extract metadata immediately when source is set so orchestrator can get it after 2.5s
                                if (mediaPlayer.Source is MediaPlaybackItem item)
                                {
                                    _host.ExtractVideoMetadata(item, mediaPlayer);
                                    // Update bitrate from adaptive source during playback
                                    _host.UpdateBitrateFromAdaptiveSource(item);
                                }

                                currentPlaybackUrl = url;
                                // Do NOT zero liveHlsRecoveries here. Reset only in AttachViaSession
                                // (intentional switch/start). Network-only recoverable classification +
                                // this counter keeps MaxLiveHlsRecoveries effective.
                                // Ready waits for the picture (PlaybackState Playing): see ArmReadyOnPlaying.

                                // Ensure the grid is visible and hit testable
                                if (playerGrid is { } overlay)
                                {
                                    overlay.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                                    overlay.IsHitTestVisible = true;
                                }

                                ShowPlayerOverlay();
                                _host._logger.LogInformation($"Playback source attached for {_title}");

                                // Force layout update
                                nativeWindow?.Activate();
                            }
                            catch (Exception ex)
                            {
                                if (IsStaleAttach(generation))
                                    return;

                                _host._logger.LogError(ex, "Failed to attach playback source");
                                engine.Raise(MediaEngineEvent.Error(session.Snapshot.AttachGeneration,
                                    $"Failed to attach playback source: {ex.Message}"));
                            }
                        });

                        // Do not set success result here. We wait for user close or media events;
                    }
                    finally
                    {
                        try { manifestStream?.Dispose(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    if (!IsStaleAttach(generation))
                    {
                        _host._logger.LogError(ex, "Failed to start playback");
                        engine.Raise(MediaEngineEvent.Error(session.Snapshot.AttachGeneration,
                            $"Failed to start playback: {ex.Message}"));
                    }
                }
                finally
                {
                    try
                    {
                        if (!cleanupInvoked)
                        {
                            playbackSwitchLock.Release();
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }

            }

            private void ShowSwitchStatus(string message)
            {
                // Toast text only — streamSwitchInProgress is owned by TrySwitchToCurrentStreamAsync
                // (set before await / clear in finally). Setting the flag here raced late MainThread
                // callbacks past finally and left video-info stuck on Status: Switching.
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        chrome.SetContentState(PlaybackChromeContentState.Switching);
                        streamCountText.Text = message;
                        if (!chrome.IsVideoInfoVisible)
                        {
                            streamInfoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                            streamInfoHideTimer?.Stop();
                        }
                        ApplyNextButtonChrome();
                        UpdateInfo();
                    }
                    catch (Exception ex) { _host.LogIgnored("ShowSwitchStatus", ex); }
                });
            }

            private async Task TrySwitchToCurrentStreamAsync(bool force = false)
            {
                if (switchingService == null || cleanupInvoked) return;
                if (suppressIndexDrivenSwitch) return;

                try
                {
                    var current = switchingService.GetCurrentStream();
                    var plan = PlaybackUrlReusePolicy.PlanSwitch(current, DateTimeOffset.UtcNow);
                    _host._logger.LogInformation(
                        "Switching playback source (force={Force}) to {Channel}: {Plan} ({Reason})",
                        force,
                        current?.Stream?.Channel ?? "(unknown)",
                        plan.Kind,
                        plan.Reason);

                    streamSwitchInProgress = true;
                    pictureSizeSettler.Reset();
                    measuredPictureConfirmed = false;
                    try { pictureSizeSettleTimer?.Stop(); }
                    catch (Exception ex) { _host.LogIgnored("StopPictureSizeSettleTimer", ex); }
                    transferMeter.Reset();
                    MainThread.BeginInvokeOnMainThread(SyncChromeContentStateFromPlayback);
                    UpdateInfo();

                    // Make-before-break: the playing stream keeps playing while a one-shot
                    // row re-/plays (or takes its warm URL); on failure the pool index goes
                    // back to the playing row and the toast line says so.
                    await pool.SwitchToCurrentAsync(
                        force,
                        ShowSwitchStatus,
                        revert =>
                        {
                            suppressIndexDrivenSwitch = true;
                            try { revert(); }
                            finally { suppressIndexDrivenSwitch = false; }
                        });
                }
                catch (Exception ex)
                {
                    _host._logger.LogError(ex, "Stream switch failed");
                }
                finally
                {
                    streamSwitchInProgress = false;
                    MainThread.BeginInvokeOnMainThread(SyncChromeContentStateFromPlayback);
                    UpdateInfo();
                }
            }
        }
    }
}
