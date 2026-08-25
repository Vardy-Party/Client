using Microsoft.Extensions.Logging;
using VardyParty.Models;
using VardyParty.Playback;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using HttpClientWin = Windows.Web.Http.HttpClient;

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

                if (activePlaybackClient != null)
                {
                    try { activePlaybackClient.Dispose(); } catch { }
                    activePlaybackClient = null;
                }
            }

            private void CleanupMediaPlayer()
            {
                if (cleanupInvoked) return;
                cleanupInvoked = true;

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
                    MainPage.SetNativePlayerActive(false);
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
                SyncHealthyStreamCount();
                ApplyPlaybackCommand(PlaybackCommand.FromEffects(session.BeginAttach(url, usedCachedUrl, force)));
            }

            private void ApplyPlaybackCommand(PlaybackCommand cmd)
            {
                PlaybackCommandExecutor.Apply(cmd, new WindowsPlaybackCommandHost(this));
            }

            private sealed class WindowsPlaybackCommandHost(PlayerSession session) : IPlaybackCommandHost
            {
                public void BeginIndexSwitchSuppression() => session.suppressIndexDrivenSwitch = true;

                public void EndIndexSwitchSuppression() => session.suppressIndexDrivenSwitch = false;

                public void ClearCurrentResolvedUrl()
                {
                    var failed = session.switchingService?.GetCurrentStream();
                    if (failed != null)
                        failed.ResolvedM3U8Url = null;
                }

                public void RemoveCurrentFromPool() => session.switchingService?.RemoveCurrentStream();

                public void SyncHealthyStreamCount() => session.SyncHealthyStreamCount();

                public void ReportFailed(string? reason) => session.ShowStreamError(reason ?? "Playback error");

                public void ReportDeclined(string? reason) => session.ShowStreamError(reason ?? "Playback error");

                public void RaiseBuffering(bool isBuffering)
                    => session._host.BufferingStateChanged?.Invoke(session._host, isBuffering);

                public void Attach(string url, bool isRevert)
                {
                    if (isRevert)
                        session._host._logger.LogWarning("Reverting to last good stream: {Url}", url);
                    _ = session.engine.AttachAsync(url, session._requestHeaders);
                }

                public void AttachCurrentAfterRemove() => _ = session.AttachCurrentFromPoolAsync();

                public void RetryFreshResolve() => _ = session.RetryFreshResolveAsync();

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

                public void SwitchPoolToPrevious() => session.switchingService?.SwitchToPreviousStream();

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

            private async Task AttachCurrentFromPoolAsync()
            {
                try
                {
                    var current = switchingService?.GetCurrentStream();
                    if (current == null)
                        return;

                    var url = current.ResolvedM3U8Url;
                    if (string.IsNullOrWhiteSpace(url) && current.Stream != null)
                    {
                        url = await ResolveFreshM3U8Async(current);
                        if (!string.IsNullOrWhiteSpace(url))
                            current.ResolvedM3U8Url = url;
                    }

                    if (string.IsNullOrWhiteSpace(url))
                    {
                        _host._logger.LogWarning("No URL after remove — cannot attach next stream");
                        return;
                    }

                    MainThread.BeginInvokeOnMainThread(() => AttachViaSession(url, usedCachedUrl: false, force: true));
                }
                catch (Exception ex)
                {
                    _host._logger.LogError(ex, "AttachCurrentFromPoolAsync failed");
                }
            }

            private async Task RetryFreshResolveAsync()
            {
                try
                {
                    var current = switchingService?.GetCurrentStream();
                    var fresh = current != null ? await ResolveFreshM3U8Async(current) : null;
                    if (string.IsNullOrWhiteSpace(fresh) ||
                        !PlaybackPolicy.ShouldAcceptFreshM3U8(session.Snapshot.CurrentUrl, fresh))
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                            ApplyPlaybackCommand(PlaybackCommand.FromEffects(session.NotifyFreshResolveUnavailable())));
                        return;
                    }

                    if (current != null)
                        current.ResolvedM3U8Url = fresh;

                    MainThread.BeginInvokeOnMainThread(() => AttachViaSession(fresh, usedCachedUrl: false, force: true));
                }
                catch (Exception ex)
                {
                    _host._logger.LogError(ex, "RetryFreshResolveAsync failed");
                    MainThread.BeginInvokeOnMainThread(() =>
                        ApplyPlaybackCommand(PlaybackCommand.FromEffects(
                            session.NotifyFreshResolveUnavailable(ex.Message))));
                }
            }

            private async Task<string?> ResolveFreshM3U8Async(EnrichedStream current)
            {
                if (current.Stream == null)
                    return null;

                return await _host._api.ResolveM3U8ForPlaybackAsync(current.Stream, current.Referer ?? string.Empty);
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

                    // Build the adaptive source BEFORE clearing the current player source so a
                    // failed switch does not leave the UI on a black frame.
                    var client = new HttpClientWin();
                    ConfigurePlaybackHttpClient(client, _refererUrl, _requestHeaders);

                    var uri = new Uri(url);
                    var adaptiveResult = await _host.CreateAdaptiveMediaSourceAsync(client, uri);

                    if (IsStaleAttach(generation))
                    {
                        try { client.Dispose(); } catch { }
                        return;
                    }

                    if (adaptiveResult.Status != AdaptiveMediaSourceCreationStatus.Success || adaptiveResult.MediaSource == null)
                    {
                        try { client.Dispose(); } catch { }
                        throw new InvalidOperationException($"Adaptive source failed: {adaptiveResult.Status}");
                    }

                    await MainThread.InvokeOnMainThreadAsync(() => PreparePlaybackSwitchOnUiThread(generation));
                    if (IsStaleAttach(generation))
                    {
                        try { client.Dispose(); } catch { }
                        return;
                    }

                    activePlaybackClient = client;

                    // Attach handler to fix segment content types and ensure headers
                    TypedEventHandler<AdaptiveMediaSource, AdaptiveMediaSourceDownloadRequestedEventArgs> downloadHandler = async (sender, args) =>
                    {
                        if (IsStaleAttach(generation))
                            return;

                        // Intercept Manifest, MediaSegment, and InitializationSegment
                        if (args.ResourceType == AdaptiveMediaSourceResourceType.Manifest ||
                            args.ResourceType == AdaptiveMediaSourceResourceType.MediaSegment ||
                            args.ResourceType == AdaptiveMediaSourceResourceType.InitializationSegment)
                        {
                            var deferral = args.GetDeferral();
                            try
                            {
                                if (IsStaleAttach(generation))
                                    return;
                                var request = new global::Windows.Web.Http.HttpRequestMessage(global::Windows.Web.Http.HttpMethod.Get, args.ResourceUri);
                                ApplyPlaybackRequestHeaders(request, _refererUrl, _requestHeaders);

                                var response = await client.SendRequestAsync(request);
                                response.EnsureSuccessStatusCode();

                                var contentType = response.Content.Headers.ContentType?.ToString();
                                var path = args.ResourceUri.AbsolutePath;

                                // Force correct content types
                                if (args.ResourceType == AdaptiveMediaSourceResourceType.MediaSegment)
                                {
                                    // If it's a media segment, force video/MP2T if it's not a valid video type
                                    // Many servers return text/plain or application/octet-stream for .ts
                                    if (string.IsNullOrEmpty(contentType) ||
                                        contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                                        contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ||
                                        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                                    {
                                        contentType = "video/MP2T";
                                    }
                                }
                                else if (args.ResourceType == AdaptiveMediaSourceResourceType.Manifest)
                                {
                                    if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                                    {
                                        contentType = "application/vnd.apple.mpegurl";
                                    }
                                }

                                args.Result.InputStream = await response.Content.ReadAsInputStreamAsync();
                                args.Result.ContentType = contentType;
                                session.NotifyDownloadSuccess();
                            }
                            catch (Exception ex)
                            {
                                var statusCode = 0;
                                try
                                {
                                    var msg = ex.Message?.ToLowerInvariant() ?? string.Empty;
                                    if (msg.Contains("404")) statusCode = 404;
                                    else if (msg.Contains("502")) statusCode = 502;
                                    else if (msg.Contains("503")) statusCode = 503;
                                    else if (msg.Contains("403")) statusCode = 403;
                                    else if (msg.Contains("401")) statusCode = 401;
                                    else if (msg.Contains("500")) statusCode = 500;
                                }
                                catch (Exception statusEx)
                                {
                                    _host._logger.LogWarning(statusEx, "Failed to parse download status");
                                }

                                _host._logger.LogWarning(
                                    ex,
                                    "Segment download failed ({ResourceType}, status={StatusCode})",
                                    args.ResourceType,
                                    statusCode);

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
                                deferral.Complete();
                            }
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