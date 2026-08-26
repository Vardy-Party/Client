using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Ports;
using VardyParty.Streaming;

namespace VardyParty.Desktop.Services;

/// <summary>
/// LibVLC playback for the Linux/desktop head, ported from the retired
/// VardyParty.Linux head's LinuxVideoPlayerService.
///
/// Rendering surface: the head's UI is MAUI XAML drawn by the Avalonia 12
/// preview backend. LibVLCSharp.Avalonia 3.10.x does support Avalonia 12, but
/// its VideoView is an Avalonia control and the MAUI-Avalonia preview has no
/// handler for hosting arbitrary Avalonia controls inside the MAUI visual
/// tree. So no drawable is attached to the MediaPlayer and libvlc opens its
/// own native video output window — the same "playback happens on a dedicated
/// native surface" model the Android head uses with NativeVideoActivity. The
/// in-app "Now playing" overlay (DesktopHomePage) carries the Close control.
///
/// LibVLC initialisation is lazy (first PlayVideoAsync), never in the startup
/// path: machines without libvlc installed (or headless CI) get a logged
/// playback error instead of a startup crash.
/// </summary>
public class DesktopVideoPlayerService : INativeVideoPlayerService, IDisposable
{
    private readonly ILogger<DesktopVideoPlayerService> _logger;
    private readonly IStreamSwitchingService _switching;
    private readonly IStreamHealthReporter _healthReporter;
    private readonly PlaybackSessionController _session = new();
    private readonly DelegatingMediaEngine _engine = new();
    private readonly PlaybackPoolCommandActions _pool;
    private readonly object _initLock = new();
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private TaskCompletionSource<PlaybackResult>? _playbackTcs;
    private Func<Task>? _onNextStreamRequested;
    private bool _isBuffering;
    private bool _initFailed;
    private EventHandler<LogEventArgs>? _libVlcLogHandler;
    private bool _libVlcLogAttached;
    private string? _refererUrl;
    private IReadOnlyDictionary<string, string>? _requestHeaders;
    private Timer? _metricsTimer;

    public event EventHandler<bool>? BufferingStateChanged;
    public event EventHandler<bool>? PlaybackVisibilityChanged;

    public DesktopVideoPlayerService(
        ILogger<DesktopVideoPlayerService> logger,
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
            _logger.LogWarning(ex, "[DesktopVideoPlayerService] Error while stopping playback");
        }
    }

    /// <summary>Lazy libvlc bring-up; returns false (never throws) when libvlc is unavailable.</summary>
    private bool TryEnsurePlayer()
    {
        lock (_initLock)
        {
            if (_mediaPlayer != null)
                return true;
            if (_initFailed)
                return false;

            try
            {
                Core.Initialize();

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

                _mediaPlayer = new MediaPlayer(_libVLC);
                _mediaPlayer.Playing += OnPlaying;
                _mediaPlayer.Buffering += OnBuffering;
                _mediaPlayer.EncounteredError += OnEncounteredError;
                _mediaPlayer.EndReached += OnEndReached;

                _logger.LogInformation("[DesktopVideoPlayerService] LibVLC initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                _initFailed = true;
                _logger.LogError(ex,
                    "[DesktopVideoPlayerService] Failed to initialize LibVLC — is the system libvlc installed (e.g. apt install vlc)?");
                return false;
            }
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
        _logger.LogInformation("[DesktopVideoPlayerService] Playing video: {Title}", title);
        _logger.LogInformation("[DesktopVideoPlayerService] URL: {Url}", m3u8Url);
        _logger.LogInformation("[DesktopVideoPlayerService] Referer: {Referer}", refererUrl);

        if (!TryEnsurePlayer())
        {
            return new PlaybackResult
            {
                Success = false,
                Message = "Video playback is unavailable: libvlc could not be initialized. Install VLC (libvlc) and try again."
            };
        }

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
            _logger.LogError(ex, "[DesktopVideoPlayerService] Error during playback");
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
            _logger.LogWarning(ex, "[DesktopVideoPlayerService] DispatchEngine failed ({Kind})", engineEvent.Kind);
        }
    }

    private void AttachViaSession(string url, bool usedCachedUrl = false, bool force = false)
    {
        _session.SetHealthyStreamCount(_switching.GetHealthyStreams().Count);
        ApplyPlaybackCommand(PlaybackCommand.FromEffects(_session.BeginAttach(url, usedCachedUrl, force)));
    }

    private void ApplyPlaybackCommand(PlaybackCommand cmd)
    {
        PlaybackCommandExecutor.Apply(cmd, new DesktopPlaybackCommandHost(this));
    }

    private sealed class DesktopPlaybackCommandHost(DesktopVideoPlayerService player) : IPlaybackCommandHost
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
            player._logger.LogWarning("[DesktopVideoPlayerService] Stream failed: {Reason}", reason);
            _ = player._healthReporter.ReportPlaybackErrorAsync(
                player._session.Snapshot.CurrentUrl, player._refererUrl, error: reason);
        }

        public void ReportDeclined(string? reason)
        {
            player._logger.LogWarning("[DesktopVideoPlayerService] Stream declined: {Reason}", reason);
            _ = player._healthReporter.ReportPlaybackErrorAsync(
                player._session.Snapshot.CurrentUrl, player._refererUrl, error: reason);
        }

        public void ReportWorking()
        {
            player._logger.LogInformation("[DesktopVideoPlayerService] Stream established");
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
                player._logger.LogWarning("[DesktopVideoPlayerService] Reverting to last good stream: {Url}", url);
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
            => player._logger.LogWarning(exception, "[DesktopVideoPlayerService] ApplyPlaybackCommand failed");
    }

    private Task AttachLibVlcAsync(
        string m3u8Url,
        IReadOnlyDictionary<string, string>? requestHeaders,
        CancellationToken cancellationToken)
    {
        if (!TryEnsurePlayer())
            throw new InvalidOperationException("LibVLC is not initialized");

        _currentMedia?.Dispose();
        _mediaPlayer!.Stop();

        var mediaLibVlc = _libVLC ?? throw new InvalidOperationException("LibVLC is not initialized");
        _currentMedia = new Media(mediaLibVlc, new Uri(m3u8Url));

        if (!string.IsNullOrWhiteSpace(_refererUrl))
            _currentMedia.AddOption($":http-referrer={_refererUrl}");

        _currentMedia.AddOption(":http-user-agent=Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _currentMedia.AddOption(":avcodec-hw=any");

        PlaybackVisibilityChanged?.Invoke(this, true);
        _mediaPlayer.Play(_currentMedia);
        _logger.LogInformation("[DesktopVideoPlayerService] Requested LibVLC attach for {Url}", m3u8Url);
        return Task.CompletedTask;
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        _logger.LogInformation("[DesktopVideoPlayerService] Playback started");
        PlaybackVisibilityChanged?.Invoke(this, true);
        SetBufferingState(false);
        _engine.Raise(MediaEngineEvent.Ready(_session.Snapshot.AttachGeneration));
        StartMetricsLoop();
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        var isBuffering = e.Cache < 100f;
        _logger.LogDebug("[DesktopVideoPlayerService] Buffering: {Percentage}%", e.Cache);
        SetBufferingState(isBuffering);
        _engine.Raise(MediaEngineEvent.Buffering(_session.Snapshot.AttachGeneration, isBuffering));
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        _logger.LogError("[DesktopVideoPlayerService] Playback error encountered");
        StopMetricsLoop();
        _engine.Raise(MediaEngineEvent.Error(_session.Snapshot.AttachGeneration, "Stream playback failed"));
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        _logger.LogInformation("[DesktopVideoPlayerService] Playback ended");
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

            var videoTrack = _mediaPlayer.VideoTrack;
            if (videoTrack <= 0)
            {
                return null;
            }

            // LibVLC doesn't directly expose resolution/framerate during playback.
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
            _logger.LogWarning(ex, "[DesktopVideoPlayerService] Error getting playback metrics");
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
                _logger.LogDebug(ex, "[DesktopVideoPlayerService] Metrics raise failed");
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
        _logger.LogInformation("[DesktopVideoPlayerService] Disposing");
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
            _logger.LogWarning(ex, "[DesktopVideoPlayerService] Error during disposal");
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
                    _logger.LogError("[DesktopVideoPlayerService] {Message}", renderedMessage);
                    return;
                }

                if (IsLibVlcWarningLevel(level))
                {
                    _logger.LogWarning("[DesktopVideoPlayerService] {Message}", renderedMessage);
                    return;
                }

                if (IsRenderDiagnosticInteresting(message))
                {
                    _logger.LogInformation("[DesktopVideoPlayerService] {Message}", renderedMessage);
                }
            }
            catch
            {
            }
        };

        _libVLC.Log += _libVlcLogHandler;
        _libVlcLogAttached = true;
        _logger.LogInformation("[DesktopVideoPlayerService] LibVLC native diagnostics enabled");
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
