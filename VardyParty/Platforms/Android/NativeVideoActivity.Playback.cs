#if ANDROID
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VardyParty.Models;
using VardyParty.Playback;

namespace VardyParty.Platforms.Android
{
    public partial class NativeVideoActivity
    {
        private readonly PlaybackSessionController _session = new();
        private readonly DelegatingMediaEngine _engine = new();
        private bool _suppressIndexDrivenSwitch;

        private long CurrentAttachGeneration => _session.Snapshot.AttachGeneration;

        private void SyncHealthyStreamCount()
        {
            _session.SetHealthyStreamCount(_switching?.GetHealthyStreams().Count ?? 0);
        }

        private void DispatchEngine(MediaEngineEvent engineEvent)
        {
            try
            {
                ApplyPlaybackCommand(PlaybackCommand.FromEffects(_session.Handle(engineEvent)));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] DispatchEngine failed ({Kind})", engineEvent.Kind);
            }
        }

        private void AttachViaSession(string url, bool usedCachedUrl = false, bool force = false)
        {
            SyncHealthyStreamCount();
            ApplyPlaybackCommand(PlaybackCommand.FromEffects(_session.BeginAttach(url, usedCachedUrl, force)));
        }

        private void ApplyPlaybackCommand(PlaybackCommand cmd)
        {
            PlaybackCommandExecutor.Apply(cmd, new NativePlaybackCommandHost(this));
        }

        private sealed class NativePlaybackCommandHost(NativeVideoActivity activity) : IPlaybackCommandHost
        {
            public void BeginIndexSwitchSuppression() => activity._suppressIndexDrivenSwitch = true;

            public void EndIndexSwitchSuppression() => activity._suppressIndexDrivenSwitch = false;

            public void ClearCurrentResolvedUrl()
            {
                var failed = activity._switching?.GetCurrentStream();
                if (failed != null)
                {
                    failed.ResolvedM3U8Url = null;
                }
            }

            public void RemoveCurrentFromPool() => activity._switching?.RemoveCurrentStream();

            public void SyncHealthyStreamCount() => activity.SyncHealthyStreamCount();

            public void ReportFailed(string? reason) => activity.PostHealthError(reason);

            public void ReportDeclined(string? reason) => activity.PostHealthError(reason);

            public void RaiseBuffering(bool isBuffering)
            {
                AndroidVideoPlayerService.ReportBufferingState(isBuffering);
                if (isBuffering)
                {
                    activity.PostHealthBuffering();
                }
            }

            public void Attach(string url, bool isRevert)
            {
                if (isRevert)
                {
                    activity._logger?.LogWarning("[NativeVideoActivity] Reverting to last good stream: {Url}", url);
                }

                _ = activity._engine.AttachAsync(url);
            }

            public void AttachCurrentAfterRemove() => _ = activity.AttachCurrentFromPoolAsync();

            public void RetryFreshResolve() => _ = activity.RetryFreshResolveAsync();

            public void StopEngine() => activity.StopAndReleasePlayer(release: false);

            public void CloseSession(string reason)
            {
                activity._logger?.LogWarning("[NativeVideoActivity] Closing playback session: {Reason}", reason);
                activity.ReportPlaybackClosed(reason);
                activity.Finish();
            }

            public void SwitchPoolToNext() => _ = AndroidVideoPlayerService.RequestNextStream();

            public void SwitchPoolToPrevious() => activity._switching?.SwitchToPreviousStream();

            public void NotifyApplyFailed(Exception exception)
                => activity._logger?.LogWarning(exception, "[NativeVideoActivity] ApplyPlaybackCommand failed");
        }

        private async Task AttachCurrentFromPoolAsync()
        {
            try
            {
                var current = _switching?.GetCurrentStream();
                if (current == null)
                {
                    return;
                }

                var url = current.ResolvedM3U8Url;
                if (string.IsNullOrWhiteSpace(url) && current.Stream != null)
                {
                    url = await ResolveFreshM3U8Async(current);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        current.ResolvedM3U8Url = url;
                    }
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    _logger?.LogWarning("[NativeVideoActivity] No URL after remove — cannot attach next stream");
                    return;
                }

                RunOnUiThread(() => AttachViaSession(url, usedCachedUrl: false, force: true));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] AttachCurrentFromPoolAsync failed");
            }
        }

        private async Task RetryFreshResolveAsync()
        {
            try
            {
                var current = _switching?.GetCurrentStream();
                var fresh = current != null ? await ResolveFreshM3U8Async(current) : null;
                if (string.IsNullOrWhiteSpace(fresh) ||
                    string.Equals(fresh, _m3u8Url, StringComparison.OrdinalIgnoreCase))
                {
                    RunOnUiThread(() =>
                        ApplyPlaybackCommand(PlaybackCommand.FromEffects(_session.NotifyFreshResolveUnavailable())));
                    return;
                }

                if (current != null)
                {
                    current.ResolvedM3U8Url = fresh;
                }

                RunOnUiThread(() => AttachViaSession(fresh, usedCachedUrl: false, force: true));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] RetryFreshResolveAsync failed");
                RunOnUiThread(() =>
                    ApplyPlaybackCommand(PlaybackCommand.FromEffects(
                        _session.NotifyFreshResolveUnavailable(ex.Message))));
            }
        }

        private async Task<string?> ResolveFreshM3U8Async(EnrichedStream current)
        {
            if (current.Stream == null)
            {
                return null;
            }

            if (_api == null)
            {
                return null;
            }

            return await _api.ResolveM3U8ForPlaybackAsync(
                current.Stream,
                current.Referer ?? string.Empty);
        }

        private void PostHealthError(string? error)
        {
            if (_healthReporter == null) return;
            try
            {
                _ = _healthReporter.ReportPlaybackErrorAsync(_m3u8Url, _refererUrl, error: error);
            }
            catch (Exception ex) { LogIgnored("ReportPlaybackError", ex); }
        }

        private void PostHealthBuffering()
        {
            if (_healthReporter == null) return;
            try
            {
                var metrics = BuildPlaybackMetrics(isBuffering: true);
                _ = _healthReporter.ReportBufferingAsync(_m3u8Url, _refererUrl, metrics: metrics);
            }
            catch (Exception ex) { LogIgnored("ReportBuffering", ex); }
        }

        /// <summary>ExoPlayer attach only — policy decisions go through <see cref="AttachViaSession"/>.</summary>
        private void AttachEngine(string m3u8Url)
        {
            try
            {
                RunOnUiThread(() =>
                {
                    try
                    {
                        if (_player == null)
                        {
                            _logger?.LogWarning("[NativeVideoActivity] Player null - cannot switch");
                            return;
                        }

                        _isPreparing = true;
                        _m3u8Url = m3u8Url;

                        var dataSourceFactory = new AndroidX.Media3.DataSource.DefaultHttpDataSource.Factory();
                        try
                        {
                            var headers = new System.Collections.Generic.Dictionary<string, string?>
                            {
                                ["Referer"] = _refererUrl ?? string.Empty,
                                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
                            };
                            try
                            {
                                _logger?.LogInformation(
                                    "[NativeVideoActivity] Playing stream. m3u8={Url} Referer={Referer} UserAgent={UA}",
                                    m3u8Url,
                                    headers["Referer"],
                                    headers["User-Agent"]);
                            }
                            catch (Exception ex) { LogIgnored("LogPlaybackHeaders", ex); }

                            var customFactory = new HeaderInjectingDataSourceFactory(headers);
                            var mediaSourceFactory = new AndroidX.Media3.ExoPlayer.Hls.HlsMediaSource.Factory(customFactory);
                            var headerBuilder = new AndroidX.Media3.Common.MediaItem.Builder();
                            headerBuilder.SetUri(m3u8Url);
                            headerBuilder.SetMimeType(AndroidX.Media3.Common.MimeTypes.ApplicationM3u8);
                            var headerItem = headerBuilder.Build()
                                ?? throw new InvalidOperationException("MediaItem.Build returned null.");
                            var headerSource = mediaSourceFactory.CreateMediaSource(headerItem)
                                ?? throw new InvalidOperationException("CreateMediaSource returned null.");
                            if (_player is not { } headerPlayer)
                                return;
                            headerPlayer.SetMediaSource(headerSource);
                            headerPlayer.Prepare();
                            headerPlayer.PlayWhenReady = true;
                            if (_playerListener != null)
                            {
                                headerPlayer.RemoveListener(_playerListener);
                                headerPlayer.AddListener(_playerListener);
                            }

                            _logger?.LogInformation(
                                "[NativeVideoActivity] Requested player to switch to {Url} (with header-injecting factory)",
                                m3u8Url);
                            return;
                        }
                        catch (Exception ex)
                        {
                            LogIgnored("HeaderInjectingFactory", ex);
                            try { dataSourceFactory.SetUserAgent("VardyParty/1.0"); } catch (Exception uaEx) { LogIgnored("SetUserAgent", uaEx); }
                        }

                        try
                        {
                            _logger?.LogInformation(
                                "[NativeVideoActivity] Playing stream (fallback factory). m3u8={Url} Referer={Referer} UserAgent={UA}",
                                m3u8Url,
                                _refererUrl ?? string.Empty,
                                "VardyParty/1.0");
                        }
                        catch (Exception ex) { LogIgnored("LogFallbackPlaybackHeaders", ex); }

                        var fallbackBuilder = new AndroidX.Media3.Common.MediaItem.Builder();
                        fallbackBuilder.SetUri(m3u8Url);
                        fallbackBuilder.SetMimeType(AndroidX.Media3.Common.MimeTypes.ApplicationM3u8);
                        var fallbackItem = fallbackBuilder.Build()
                            ?? throw new InvalidOperationException("MediaItem.Build returned null.");
                        var mediaSource = new AndroidX.Media3.ExoPlayer.Hls.HlsMediaSource.Factory(dataSourceFactory)
                            .CreateMediaSource(fallbackItem)
                            ?? throw new InvalidOperationException("CreateMediaSource returned null.");

                        if (_player is not { } fallbackPlayer)
                            return;
                        fallbackPlayer.SetMediaSource(mediaSource);
                        fallbackPlayer.Prepare();
                        fallbackPlayer.PlayWhenReady = true;

                        if (_playerListener != null)
                        {
                            fallbackPlayer.RemoveListener(_playerListener);
                            fallbackPlayer.AddListener(_playerListener);
                        }

                        _logger?.LogInformation("[NativeVideoActivity] Requested player to switch to {Url}", m3u8Url);
                    }
                    catch (Exception ex)
                    {
                        _isPreparing = false;
                        _logger?.LogError(ex, "[NativeVideoActivity] AttachEngine failed");
                        var generation = CurrentAttachGeneration;
                        var message = ex.Message;
                        _playerView?.Post(() => _engine.Raise(MediaEngineEvent.Error(generation, message)));
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[NativeVideoActivity] AttachEngine outer exception");
            }
        }
    }
}
#endif
