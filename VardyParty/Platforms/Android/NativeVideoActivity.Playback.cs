#if ANDROID
using System;
using System.Threading;
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
        private PlaybackPoolCommandActions? _pool;
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

        internal void EnsurePool()
        {
            if (_switching == null)
            {
                _pool = null;
                return;
            }

            _pool = new PlaybackPoolCommandActions(
                _session,
                _switching,
                ResolveFreshM3U8Async,
                (url, usedCached, force) => RunOnUiThread(() => AttachViaSession(url, usedCached, force)),
                cmd => RunOnUiThread(() => ApplyPlaybackCommand(cmd)));
        }

        private sealed class NativePlaybackCommandHost(NativeVideoActivity activity) : IPlaybackCommandHost
        {
            public void BeginIndexSwitchSuppression() => activity._suppressIndexDrivenSwitch = true;

            public void EndIndexSwitchSuppression() => activity._suppressIndexDrivenSwitch = false;

            public void ClearCurrentResolvedUrl() => activity._pool?.ClearCurrentResolvedUrl();

            public void RemoveCurrentFromPool() => activity._pool?.RemoveCurrentFromPool();

            public void SyncHealthyStreamCount() => activity._pool?.SyncHealthyStreamCount();

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

            public void AttachCurrentAfterRemove() => _ = activity._pool?.AttachCurrentFromPoolAsync();

            public void RetryFreshResolve() => _ = activity._pool?.RetryFreshResolveAsync();

            public void StopEngine() => activity.StopAndReleasePlayer(release: false);

            public void CloseSession(string reason)
            {
                activity._logger?.LogWarning("[NativeVideoActivity] Closing playback session: {Reason}", reason);
                activity.ReportPlaybackClosed(reason);
                activity.Finish();
            }

            public void SwitchPoolToNext() => _ = AndroidVideoPlayerService.RequestNextStream();

            public void SwitchPoolToPrevious() => activity._pool?.SwitchPoolToPrevious();

            public void NotifyApplyFailed(Exception exception)
                => activity._logger?.LogWarning(exception, "[NativeVideoActivity] ApplyPlaybackCommand failed");
        }

        private Task<string?> ResolveFreshM3U8Async(EnrichedStream current, CancellationToken cancellationToken)
        {
            if (current.Stream == null || _api == null)
                return Task.FromResult<string?>(null);

            return _api.ResolveM3U8ForPlaybackAsync(
                current.Stream,
                current.Referer ?? string.Empty,
                cancellationToken);
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
