using Microsoft.Extensions.Logging;
using System.Threading;
using VardyParty.Hosting;
using VardyParty.Kernel;
using VardyParty.Playback;
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
                CancelPlaybackDownloads();

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

                public void ReportFailed(string? reason) => session.ShowStreamError(reason ?? "Playback error");

                public void ReportDeclined(string? reason) => session.ShowStreamError(reason ?? "Playback error");

                public void ReportWorking()
                {
                    var url = session.session.Snapshot.CurrentUrl;
                    var stream = session.switchingService?.GetCurrentStream()?.Stream;
                    var streamName = stream == null ? null : StreamHealthIdentity.GetStreamName(stream);
                    _ = session._host._healthReporter.ReportPlaybackStartedAsync(
                        url,
                        session._refererUrl,
                        streamName,
                        metrics: session._host.GetCurrentMetrics());
                }

                public void MarkEstablished()
                {
                    // Session established flag is owned by PlaybackSessionController.Handle(Ready).
                }

                public void RaiseBuffering(bool isBuffering)
                    => session._host.BufferingStateChanged?.Invoke(session._host, isBuffering);

                public void Attach(string url, bool isRevert)
                {
                    if (isRevert)
                        session._host._logger.LogWarning("Reverting to last good stream: {Url}", url);
                    _ = session.engine.AttachAsync(url, session._requestHeaders);
                }

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
                try
                {
                    mediaPlayer.Pause();
                    mediaPlayer.Source = null;
                    _host._currentPlaybackItem = null;
                }
                catch { }
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
                    var created = await _host.CreateAdaptiveMediaSourceAsync(
                        managedHttp,
                        uri,
                        _refererUrl,
                        _requestHeaders,
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

                                managedRequest = new System.Net.Http.HttpRequestMessage(
                                    System.Net.Http.HttpMethod.Get,
                                    args.ResourceUri.ToString());
                                ApplyManagedPlaybackHeaders(managedRequest, _refererUrl, _requestHeaders);
                                ApplyResourceByteRange(managedRequest, args);

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
                                var winrtStream = await CopyToRandomAccessStreamAsync(managedResponse, segmentToken);
                                if (IsStaleAttach(generation) || segmentToken.IsCancellationRequested)
                                {
                                    winrtStream.Dispose();
                                    return;
                                }

                                if (!TryRootPlaybackStream(winrtStream, generation))
                                    return;

                                args.Result.InputStream = winrtStream.GetInputStreamAt(0);
                                args.Result.ContentType = contentType;
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

                            var failureMessage =
                                $"Segment download failed ({args.ResourceType}, status={statusCode})";
                            var downloadCmd = PlaybackCommand.FromEffects(
                                session.NotifyDownloadFailure(failureMessage));
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
                                args.Result.ExtendedStatus = statusCode > 0 ? (uint)statusCode : 1;
                            }
                            catch (Exception statusEx)
                            {
                                _host._logger.LogWarning(statusEx, "Failed to set download extended status");
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
                            // Do NOT zero liveHlsRecoveries here: soft-reattach Ready fires as soon as
                            // MediaPlaybackItem is assigned (before sustained play). Reset only in
                            // AttachViaSession (intentional switch/start). Network-only recoverable
                            // classification + this counter keeps MaxLiveHlsRecoveries effective.
                            engine.Raise(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

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

            private async Task TrySwitchToCurrentStreamAsync(bool force = false)
            {
                if (switchingService == null || cleanupInvoked) return;
                if (suppressIndexDrivenSwitch) return;

                try
                {
                    var current = switchingService.GetCurrentStream();
                    var url = current?.ResolvedM3U8Url;
                    if (string.IsNullOrWhiteSpace(url)) return;
                    if (!force && string.Equals(currentPlaybackUrl, url, StringComparison.OrdinalIgnoreCase)) return;
                    _host._logger.LogInformation($"Switching playback source (force={force})");
                    AttachViaSession(url, usedCachedUrl: false, force: force);
                }
                catch (Exception ex)
                {
                    _host._logger.LogError(ex, "Stream switch failed");
                }
            }
        }
    }
}