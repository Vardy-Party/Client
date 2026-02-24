using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VardyParty.Models;
using VardyParty.Services;

namespace VardyParty.Linux.Services;

public class LinuxVideoPlayerService : INativeVideoPlayerService, IDisposable
{
    private readonly ILogger<LinuxVideoPlayerService> _logger;
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private TaskCompletionSource<PlaybackResult>? _playbackTcs;
    private Func<Task>? _onNextStreamRequested;
    private bool _isBuffering;
    private readonly bool _isWsl;

    public event EventHandler<bool>? BufferingStateChanged;

    public LinuxVideoPlayerService(ILogger<LinuxVideoPlayerService> logger)
    {
        _logger = logger;
        _isWsl = IsRunningOnWsl();
        InitializeLibVLC();
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
                "--http-reconnect"               // Auto-reconnect on network issues
            };


            if (_isWsl)
            {
                Environment.SetEnvironmentVariable("LIBGL_ALWAYS_SOFTWARE", "1");
                Environment.SetEnvironmentVariable("MESA_LOADER_DRIVER_OVERRIDE", "llvmpipe");

                vlcOptions.Add("--avcodec-hw=none");
                vlcOptions.Add("--vout=xcb");

                _logger.LogWarning("[LinuxVideoPlayerService] WSL environment detected; forcing software rendering, disabling hardware decode, and using --vout=xcb");
            }

            _libVLC = new LibVLC(vlcOptions.ToArray());

            _logger.LogInformation("[LinuxVideoPlayerService] LibVLC initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LinuxVideoPlayerService] Failed to initialize LibVLC");
            throw;
        }
    }

    public async Task<PlaybackResult> PlayVideoAsync(
        string m3u8Url,
        string refererUrl,
        string title,
        Func<Task>? onNextStreamRequested = null)
    {
        _logger.LogInformation("[LinuxVideoPlayerService] Playing video: {Title}", title);
        _logger.LogInformation("[LinuxVideoPlayerService] URL: {Url}", m3u8Url);
        _logger.LogInformation("[LinuxVideoPlayerService] Referer: {Referer}", refererUrl);

        _onNextStreamRequested = onNextStreamRequested;
        _playbackTcs = new TaskCompletionSource<PlaybackResult>();

        try
        {
            // Clean up previous media if exists
            _currentMedia?.Dispose();
            _mediaPlayer?.Stop();

            // Create media player if not exists
            if (_mediaPlayer == null)
            {
                var libVlc = _libVLC ?? throw new InvalidOperationException("LibVLC is not initialized");
                _mediaPlayer = new MediaPlayer(libVlc);
                _logger.LogDebug("[LinuxVideoPlayerService] MediaPlayer created. Video output should be initialized.");
                _mediaPlayer.Playing += OnPlaying;
                _mediaPlayer.Buffering += OnBuffering;
                _mediaPlayer.EncounteredError += OnEncounteredError;
                _mediaPlayer.EndReached += OnEndReached;
            }

            // Create media with options
            var mediaLibVlc = _libVLC ?? throw new InvalidOperationException("LibVLC is not initialized");
            _currentMedia = new Media(mediaLibVlc, new Uri(m3u8Url));
            
            // Set HTTP Referer header
            if (!string.IsNullOrWhiteSpace(refererUrl))
            {
                _currentMedia.AddOption($":http-referrer={refererUrl}");
            }

            // Set HTTP User-Agent
            _currentMedia.AddOption(":http-user-agent=Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            if (_isWsl)
            {
                _currentMedia.AddOption(":avcodec-hw=none");
            }

            // Parse media to get stream information
            await _currentMedia.Parse(MediaParseOptions.ParseNetwork);

            // Start playback
            _mediaPlayer.Play(_currentMedia);

            _logger.LogInformation("[LinuxVideoPlayerService] Playback started successfully");

            // Wait for playback to complete or error
            var result = await _playbackTcs.Task;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LinuxVideoPlayerService] Error during playback");
            return new PlaybackResult
            {
                Success = false,
                Message = $"Playback error: {ex.Message}"
            };
        }
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        _logger.LogInformation("[LinuxVideoPlayerService] Playback started");
        SetBufferingState(false);
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        var isBuffering = e.Cache < 100f;
        _logger.LogDebug("[LinuxVideoPlayerService] Buffering: {Percentage}%", e.Cache);
        SetBufferingState(isBuffering);
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        _logger.LogError("[LinuxVideoPlayerService] Playback error encountered");
        
        _playbackTcs?.TrySetResult(new PlaybackResult
        {
            Success = false,
            Message = "Stream playback failed"
        });
    }

    private async void OnEndReached(object? sender, EventArgs e)
    {
        _logger.LogInformation("[LinuxVideoPlayerService] Playback ended");

        // Try to play next stream if callback provided
        if (_onNextStreamRequested != null)
        {
            _logger.LogInformation("[LinuxVideoPlayerService] Requesting next stream...");
            try
            {
                await _onNextStreamRequested.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LinuxVideoPlayerService] Error requesting next stream");
            }
        }
        else
        {
            _playbackTcs?.TrySetResult(new PlaybackResult
            {
                Success = true,
                Message = "Playback completed"
            });
        }
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

    public void Dispose()
    {
        _logger.LogInformation("[LinuxVideoPlayerService] Disposing");

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

            _libVLC?.Dispose();
            _libVLC = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LinuxVideoPlayerService] Error during disposal");
        }

        GC.SuppressFinalize(this);
    }

    private static bool IsRunningOnWsl()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_INTEROP")))
        {
            return true;
        }

        try
        {
            if (File.Exists("/proc/version"))
            {
                var version = File.ReadAllText("/proc/version");
                return version.Contains("microsoft", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
