using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Streaming;

namespace VardyParty.Linux.Services
{
    public class LinuxVideoPlayerService : INativeVideoPlayerService, IDisposable
    {
        private readonly ILogger<LinuxVideoPlayerService> _logger;
        private readonly IStreamSwitchingService _switching;
        private readonly IStreamHealthReporter _healthReporter;
        private readonly PlaybackSessionController _session = new();
        private readonly DelegatingMediaEngine _engine = new();
        private readonly PlaybackPoolCommandActions _pool;
        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;
        private Media? _currentMedia;
        private TaskCompletionSource<PlaybackResult>? _playbackTcs;
        private Func<Task>? _onNextStreamRequested;
        private bool _isBuffering;
        private EventHandler<LogEventArgs>? _libVlcLogHandler;
        private bool _libVlcLogAttached;
        private string? _refererUrl;
        private IReadOnlyDictionary<string, string>? _requestHeaders;
        private Timer? _metricsTimer;

        public event EventHandler<bool>? BufferingStateChanged;
        public event EventHandler<bool>? PlaybackVisibilityChanged;

        public MediaPlayer? MediaPlayer => _mediaPlayer;

        public LinuxVideoPlayerService(
            ILogger<LinuxVideoPlayerService> logger,
            IStreamSwitchingService switching,
            ResolveFreshPlaybackUrlAsync resolveFresh,
            IStreamHealthReporter healthReporter)
        {
            _logger = logger;
            _switching = switching;
            _healthReporter = healthReporter;
            _pool = new PlaybackPoolCommandActions(
                _session,
                _switching,
                resolveFresh,
                AttachViaSession,
                ApplyPlaybackCommand);
            _engine.EngineEvent += (_, engineEvent) => DispatchEngine(engineEvent);
            _engine.MetricsHandler = GetCurrentMetrics;
            _engine.AttachHandler = AttachLibVlcAsync;
            InitializeLibVLC();
            EnsureMediaPlayer();
        }

        public void StopPlayback()
        {
            try
            {
                _mediaPlayer?.Stop();
                StopMetricsLoop();
                PlaybackVisibilityChanged?.Invoke(this, false);
                _playbackTcs?.TrySetResult(new PlaybackResult
                {
                    Success = true,
                    Message = "User closed playback"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LinuxVideoPlayerService] Error while stopping playback");
            }
        }

        private void InitializeLibVLC()
        {
            try
            {
                var vlcOptions = new List<string>
            {
                "--quiet",                       // Reduce verbose output
                "--no-video-title-show",         // Don't show video title on playback
                "--network-caching=2000",        // 2 second network cache
                "--http-reconnect",              // Auto-reconnect on network issues
                "--avcodec-hw=any",              // Prefer hardware decode on native Linux
                "--no-spdif"                     // Avoid passthrough output issues
            };

                _libVLC = new LibVLC(vlcOptions.ToArray());
                AttachLibVlcDiagnostics();

                _logger.LogInformation("[LinuxVideoPlayerService] LibVLC initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LinuxVideoPlayerService] Failed to initialize LibVLC");
                throw;
            }
        }

        public async Task<PlaybackResult> PlayVideoAsync(
            // ReSharper disable once InconsistentNaming
            string m3u8Url,
            string refererUrl,
            string title,
            Func<Task>? onNextStreamRequested = null,
            string? league = null,
            string? homeTeam = null,
            string? awayTeam = null,
            IReadOnlyDictionary<string, string>? requestHeaders = null)
        {
            _logger.LogInformation("[LinuxVideoPlayerService] Playing video: {Title}", title);
            _logger.LogInformation("[LinuxVideoPlayerService] URL: {Url}", m3u8Url);
            _logger.LogInformation("[LinuxVideoPlayerService] Referer: {Referer}", refererUrl);

            _onNextStreamRequested = onNextStreamRequested;
            _playbackTcs = new TaskCompletionSource<PlaybackResult>();
            _refererUrl = refererUrl;
            _requestHeaders = requestHeaders;

            try
            {
                _session.Reset();
                AttachViaSession(m3u8Url);
                var result = await _playbackTcs.Task;
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LinuxVideoPlayerService] Error during playback");
                PlaybackVisibilityChanged?.Invoke(this, false);
                return new PlaybackResult
                {
                    Success = false,
                    Message = $"Playback error: {ex.Message}"
                };
            }
        }

        private void DispatchEngine(MediaEngineEvent engineEvent)
        {
            try
            {
                ApplyPlaybackCommand(PlaybackCommand.FromEffects(_session.Handle(engineEvent)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LinuxVideoPlayerService] DispatchEngine failed ({Kind})", engineEvent.Kind);
            }
        }

        private void AttachViaSession(string url, bool usedCachedUrl = false, bool force = false)
        {
            _session.SetHealthyStreamCount(_switching.GetHealthyStreams().Count);
            ApplyPlaybackCommand(PlaybackCommand.FromEffects(_session.BeginAttach(url, usedCachedUrl, force)));
        }

        private void ApplyPlaybackCommand(PlaybackCommand cmd)
        {
            PlaybackCommandExecutor.Apply(cmd, new LinuxPlaybackCommandHost(this));
        }

        private sealed class LinuxPlaybackCommandHost(LinuxVideoPlayerService player) : IPlaybackCommandHost
        {
            public void BeginIndexSwitchSuppression()
            {
            }

            public void EndIndexSwitchSuppression()
            {
            }

            public void ClearCurrentResolvedUrl() => player._pool.ClearCurrentResolvedUrl();

            public void RemoveCurrentFromPool() => player._pool.RemoveCurrentFromPool();

            public void SyncHealthyStreamCount() => player._pool.SyncHealthyStreamCount();

            public void ReportFailed(string? reason)
            {
                player._logger.LogWarning("[LinuxVideoPlayerService] Stream failed: {Reason}", reason);
                _ = player._healthReporter.ReportPlaybackErrorAsync(
                    player._session.Snapshot.CurrentUrl, player._refererUrl, error: reason);
            }

            public void ReportDeclined(string? reason)
            {
                player._logger.LogWarning("[LinuxVideoPlayerService] Stream declined: {Reason}", reason);
                _ = player._healthReporter.ReportPlaybackErrorAsync(
                    player._session.Snapshot.CurrentUrl, player._refererUrl, error: reason);
            }

            public void ReportWorking()
            {
                player._logger.LogInformation("[LinuxVideoPlayerService] Stream established");
                _ = player._healthReporter.ReportPlaybackStartedAsync(
                    player._session.Snapshot.CurrentUrl,
                    player._refererUrl,
                    metrics: player.GetCurrentMetrics());
            }

            public void MarkEstablished()
            {
                // Session established flag is owned by PlaybackSessionController.Handle(Ready).
            }

            public void RaiseBuffering(bool isBuffering)
            {
                player.BufferingStateChanged?.Invoke(player, isBuffering);
                if (isBuffering)
                {
                    _ = player._healthReporter.ReportBufferingAsync(
                        player._session.Snapshot.CurrentUrl,
                        player._refererUrl,
                        metrics: player.GetCurrentMetrics());
                }
            }

            public void Attach(string url, bool isRevert)
            {
                if (isRevert)
                    player._logger.LogWarning("[LinuxVideoPlayerService] Reverting to last good stream: {Url}", url);
                _ = player._engine.AttachAsync(url, player._requestHeaders);
            }

            public void AttachCurrentAfterRemove() => _ = player._pool.AttachCurrentFromPoolAsync();

            public void RetryFreshResolve() => _ = player._pool.RetryFreshResolveAsync();

            public void StopEngine() => player._mediaPlayer?.Stop();

            public void CloseSession(string reason)
            {
                player.PlaybackVisibilityChanged?.Invoke(player, false);
                player._playbackTcs?.TrySetResult(PlaybackResult.Completed(reason, true));
            }

            public void SwitchPoolToNext()
            {
                if (player._onNextStreamRequested != null)
                    _ = player._onNextStreamRequested();
            }

            public void SwitchPoolToPrevious()
            {
                player._pool.SwitchPoolToPrevious();
                _ = player._pool.AttachCurrentFromPoolAsync();
            }

            public void NotifyApplyFailed(Exception exception)
                => player._logger.LogWarning(exception, "[LinuxVideoPlayerService] ApplyPlaybackCommand failed");
        }

        private Task AttachLibVlcAsync(
            string m3u8Url,
            IReadOnlyDictionary<string, string>? requestHeaders,
            CancellationToken cancellationToken)
        {
            _currentMedia?.Dispose();
            _mediaPlayer?.Stop();
            EnsureMediaPlayer();

            var mediaLibVlc = _libVLC ?? throw new InvalidOperationException("LibVLC is not initialized");
            _currentMedia = new Media(mediaLibVlc, new Uri(m3u8Url));

            if (!string.IsNullOrWhiteSpace(_refererUrl))
                _currentMedia.AddOption($":http-referrer={_refererUrl}");

            _currentMedia.AddOption(":http-user-agent=Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _currentMedia.AddOption(":avcodec-hw=any");

            PlaybackVisibilityChanged?.Invoke(this, true);
            var mediaPlayer = _mediaPlayer ?? throw new InvalidOperationException("MediaPlayer is not initialized");
            mediaPlayer.Play(_currentMedia);
            _logger.LogInformation("[LinuxVideoPlayerService] Requested LibVLC attach for {Url}", m3u8Url);
            return Task.CompletedTask;
        }

        private void OnPlaying(object? sender, EventArgs e)
        {
            _logger.LogInformation("[LinuxVideoPlayerService] Playback started");
            PlaybackVisibilityChanged?.Invoke(this, true);
            SetBufferingState(false);
            _engine.Raise(MediaEngineEvent.Ready(_session.Snapshot.AttachGeneration));
            StartMetricsLoop();
        }

        private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
        {
            var isBuffering = e.Cache < 100f;
            _logger.LogDebug("[LinuxVideoPlayerService] Buffering: {Percentage}%", e.Cache);
            SetBufferingState(isBuffering);
            _engine.Raise(MediaEngineEvent.Buffering(_session.Snapshot.AttachGeneration, isBuffering));
        }

        private void OnEncounteredError(object? sender, EventArgs e)
        {
            _logger.LogError("[LinuxVideoPlayerService] Playback error encountered");
            StopMetricsLoop();
            _engine.Raise(MediaEngineEvent.Error(_session.Snapshot.AttachGeneration, "Stream playback failed"));
        }

        private void OnEndReached(object? sender, EventArgs e)
        {
            _logger.LogInformation("[LinuxVideoPlayerService] Playback ended");
            StopMetricsLoop();
            _engine.Raise(MediaEngineEvent.Ended(_session.Snapshot.AttachGeneration));
            PlaybackVisibilityChanged?.Invoke(this, false);
            _playbackTcs?.TrySetResult(PlaybackResult.SuccessResult("Playback completed"));
        }

        private void SetBufferingState(bool isBuffering)
        {
            if (_isBuffering != isBuffering)
            {
                _isBuffering = isBuffering;
                BufferingStateChanged?.Invoke(this, isBuffering);
            }
        }

        private void EnsureMediaPlayer()
        {
            if (_mediaPlayer != null)
            {
                return;
            }

            var libVlc = _libVLC ?? throw new InvalidOperationException("LibVLC is not initialized");
            _mediaPlayer = new MediaPlayer(libVlc);
            _mediaPlayer.Playing += OnPlaying;
            _mediaPlayer.Buffering += OnBuffering;
            _mediaPlayer.EncounteredError += OnEncounteredError;
            _mediaPlayer.EndReached += OnEndReached;
            _logger.LogInformation("[LinuxVideoPlayerService] MediaPlayer created");
        }

        public PlaybackMetrics? GetCurrentMetrics()
        {
            if (_mediaPlayer == null || !_mediaPlayer.IsPlaying)
            {
                return null;
            }

            try
            {
                var media = _mediaPlayer.Media;
                if (media == null)
                {
                    return null;
                }

                // Get video track information
                var videoTrack = _mediaPlayer.VideoTrack;
                if (videoTrack <= 0)
                {
                    return null;
                }

                // LibVLC doesn't directly expose resolution/framerate during playback
                // We'd need to parse the track information from media
                return new PlaybackMetrics
                {
                    Resolution = null,
                    Framerate = null,
                    VideoCodec = "H.264",
                    AudioCodec = null,
                    BitrateKbps = null
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LinuxVideoPlayerService] Error getting playback metrics");
                return null;
            }
        }

        private void StartMetricsLoop()
        {
            StopMetricsLoop();
            _metricsTimer = new Timer(_ =>
            {
                try
                {
                    var metrics = GetCurrentMetrics();
                    _engine.Raise(MediaEngineEvent.Metrics(
                        _session.Snapshot.AttachGeneration,
                        metrics?.BitrateKbps,
                        _isBuffering));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[LinuxVideoPlayerService] Metrics raise failed");
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private void StopMetricsLoop()
        {
            _metricsTimer?.Dispose();
            _metricsTimer = null;
        }

        public void Dispose()
        {
            _logger.LogInformation("[LinuxVideoPlayerService] Disposing");
            StopMetricsLoop();

            try
            {
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Playing -= OnPlaying;
                    _mediaPlayer.Buffering -= OnBuffering;
                    _mediaPlayer.EncounteredError -= OnEncounteredError;
                    _mediaPlayer.EndReached -= OnEndReached;
                    _mediaPlayer.Stop();
                    _mediaPlayer.Dispose();
                    _mediaPlayer = null;
                }

                _currentMedia?.Dispose();
                _currentMedia = null;

                DetachLibVlcDiagnostics();

                _libVLC?.Dispose();
                _libVLC = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[LinuxVideoPlayerService] Error during disposal");
            }

            PlaybackVisibilityChanged?.Invoke(this, false);
            GC.SuppressFinalize(this);
        }

        private void AttachLibVlcDiagnostics()
        {
            if (_libVLC == null || _libVlcLogAttached)
            {
                return;
            }

            _libVlcLogHandler = (_, logEventArgs) =>
            {
                try
                {
                    var message = logEventArgs.Message?.Trim();
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        return;
                    }

                    var module = logEventArgs.Module?.Trim();
                    var level = logEventArgs.Level.ToString();
                    var renderedMessage = string.IsNullOrWhiteSpace(module)
                        ? $"[LibVLC:{level}] {message}"
                        : $"[LibVLC:{level}:{module}] {message}";

                    if (IsLibVlcErrorLevel(level))
                    {
                        _logger.LogError("[LinuxVideoPlayerService] {Message}", renderedMessage);
                        return;
                    }

                    if (IsLibVlcWarningLevel(level))
                    {
                        _logger.LogWarning("[LinuxVideoPlayerService] {Message}", renderedMessage);
                        return;
                    }

                    if (IsRenderDiagnosticInteresting(message))
                    {
                        _logger.LogInformation("[LinuxVideoPlayerService] {Message}", renderedMessage);
                    }
                }
                catch
                {
                }
            };

            _libVLC.Log += _libVlcLogHandler;
            _libVlcLogAttached = true;
            _logger.LogInformation("[LinuxVideoPlayerService] LibVLC native diagnostics enabled");
        }

        private void DetachLibVlcDiagnostics()
        {
            if (_libVLC == null || !_libVlcLogAttached || _libVlcLogHandler == null)
            {
                _libVlcLogAttached = false;
                _libVlcLogHandler = null;
                return;
            }

            try
            {
                _libVLC.Log -= _libVlcLogHandler;
            }
            catch
            {
            }

            _libVlcLogAttached = false;
            _libVlcLogHandler = null;
        }

        private static bool IsLibVlcErrorLevel(string level)
        {
            return level.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                   level.Contains("crit", StringComparison.OrdinalIgnoreCase) ||
                   level.Contains("alert", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLibVlcWarningLevel(string level)
        {
            return level.Contains("warn", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRenderDiagnosticInteresting(string message)
        {
            return message.Contains("egl", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("mesa", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("zink", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("vout", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("decoder", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("avcodec", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("drm", StringComparison.OrdinalIgnoreCase);
        }



    }

}



