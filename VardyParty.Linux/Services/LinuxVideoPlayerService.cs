using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VardyParty.Models;
using VardyParty.Services;

namespace VardyParty.Linux.Services
{
public class LinuxVideoPlayerService : INativeVideoPlayerService, IDisposable
{
    private readonly ILogger<LinuxVideoPlayerService> _logger;
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private TaskCompletionSource<PlaybackResult>? _playbackTcs;
    private Func<Task>? _onNextStreamRequested;
    private bool _isBuffering;
    private string? _tempManifestPath;
    private EventHandler<LogEventArgs>? _libVlcLogHandler;
    private bool _libVlcLogAttached;

    private static readonly string[] SuspiciousStreamExtensions =
    {
        ".css",
        ".woff",
        ".woff2",
        ".js",
        ".php",
        ".txt"
    };

    public event EventHandler<bool>? BufferingStateChanged;
    public event EventHandler<bool>? PlaybackVisibilityChanged;

    public MediaPlayer? MediaPlayer => _mediaPlayer;

    public LinuxVideoPlayerService(ILogger<LinuxVideoPlayerService> logger)
    {
        _logger = logger;
        InitializeLibVLC();
        EnsureMediaPlayer();
    }

    public void StopPlayback()
    {
        try
        {
            _mediaPlayer?.Stop();
            CleanupTemporaryManifest();
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
        string m3u8Url,
        string refererUrl,
        string title,
        Func<Task>? onNextStreamRequested = null)
    {
        var playbackUrl = ForceFixedTestStream ? FixedTestStreamUrl : m3u8Url;
        var playbackReferer = ForceFixedTestStream ? string.Empty : refererUrl;

        if (ForceFixedTestStream)
        {
            _logger.LogWarning("[LinuxVideoPlayerService] ForceFixedTestStream enabled; overriding stream URL for Linux playback test");
        }

        _logger.LogInformation("[LinuxVideoPlayerService] Playing video: {Title}", title);
        _logger.LogInformation("[LinuxVideoPlayerService] URL: {Url}", playbackUrl);
        _logger.LogInformation("[LinuxVideoPlayerService] Referer: {Referer}", playbackReferer);

        _onNextStreamRequested = onNextStreamRequested;
        _playbackTcs = new TaskCompletionSource<PlaybackResult>();

        try
        {
            // Clean up previous media if exists
            _currentMedia?.Dispose();
            _mediaPlayer?.Stop();
            CleanupTemporaryManifest();

            EnsureMediaPlayer();

            _logger.LogInformation("[LinuxVideoPlayerService] Using direct playback URL (recovery mode)");

            // Create media with options
            var mediaLibVlc = _libVLC ?? throw new InvalidOperationException("LibVLC is not initialized");
            _currentMedia = new Media(mediaLibVlc, new Uri(playbackUrl));
            
            // Set HTTP Referer header
            if (!string.IsNullOrWhiteSpace(playbackReferer))
            {
                _currentMedia.AddOption($":http-referrer={playbackReferer}");
            }

            // Set HTTP User-Agent
            _currentMedia.AddOption(":http-user-agent=Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _currentMedia.AddOption(":avcodec-hw=any");

            // Start playback
            var mediaPlayer = _mediaPlayer ?? throw new InvalidOperationException("MediaPlayer is not initialized");
            mediaPlayer.Play(_currentMedia);

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
        PlaybackVisibilityChanged?.Invoke(this, true);
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
        CleanupTemporaryManifest();
        PlaybackVisibilityChanged?.Invoke(this, false);
        
        _playbackTcs?.TrySetResult(new PlaybackResult
        {
            Success = false,
            Message = "Stream playback failed"
        });
    }

    private async void OnEndReached(object? sender, EventArgs e)
    {
        _logger.LogInformation("[LinuxVideoPlayerService] Playback ended");
        CleanupTemporaryManifest();

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
            PlaybackVisibilityChanged?.Invoke(this, false);
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
            CleanupTemporaryManifest();

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

    private async Task<PreparedPlaybackSource> PreparePlaybackSourceAsync(string requestedUrl, string refererUrl)
    {
        if (string.IsNullOrWhiteSpace(requestedUrl))
        {
            return PreparedPlaybackSource.Failure("Playback URL is empty");
        }

        if (!Uri.TryCreate(requestedUrl, UriKind.Absolute, out var requestedUri))
        {
            return PreparedPlaybackSource.Failure($"Invalid playback URL: {requestedUrl}");
        }

        if (!ShouldProbeStreamUrl(requestedUri))
        {
            return PreparedPlaybackSource.CreateSuccess(requestedUrl, false, false, false);
        }

        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };

            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            if (!string.IsNullOrWhiteSpace(refererUrl))
            {
                client.DefaultRequestHeaders.Referrer = Uri.TryCreate(refererUrl, UriKind.Absolute, out var refererUri)
                    ? refererUri
                    : null;
            }

            using var response = await client.GetAsync(requestedUri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var finalUri = response.RequestMessage?.RequestUri ?? requestedUri;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            var responseBytes = await response.Content.ReadAsByteArrayAsync();
            var prefixLength = Math.Min(responseBytes.Length, 1024);
            var bodyPrefix = prefixLength > 0
                ? Encoding.UTF8.GetString(responseBytes, 0, prefixLength)
                : string.Empty;
            var detectedManifest = LooksLikeM3U8(bodyPrefix, contentType, finalUri);

            _logger.LogInformation(
                "[LinuxVideoPlayerService] Stream probe: requested={RequestedUrl}; final={FinalUrl}; contentType={ContentType}; manifestDetected={ManifestDetected}",
                requestedUrl,
                finalUri,
                string.IsNullOrWhiteSpace(contentType) ? "<none>" : contentType,
                detectedManifest);

            if (!detectedManifest)
            {
                return PreparedPlaybackSource.CreateSuccess(finalUri.ToString(), false, false, false);
            }

            return PreparedPlaybackSource.CreateSuccess(finalUri.ToString(), false, true, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LinuxVideoPlayerService] Stream probe failed; falling back to original URL");
            return PreparedPlaybackSource.CreateSuccess(requestedUrl, false, false, false);
        }
    }

    private static bool ShouldProbeStreamUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var extension in SuspiciousStreamExtensions)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return true;
    }

    private static bool LooksLikeM3U8(string bodyPrefix, string contentType, Uri finalUri)
    {
        if (finalUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(contentType) &&
            (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
             contentType.Contains("vnd.apple", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return bodyPrefix.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeManifestText(string manifestText, Uri manifestUri)
    {
        if (string.IsNullOrWhiteSpace(manifestText))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        using var reader = new StringReader(manifestText);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine(line ?? string.Empty);
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                sb.AppendLine(NormalizeManifestTagUris(line, manifestUri));
                continue;
            }

            if (Uri.TryCreate(line, UriKind.Absolute, out _))
            {
                sb.AppendLine(line);
                continue;
            }

            var absolute = new Uri(manifestUri, line);
            sb.AppendLine(absolute.ToString());
        }

        return sb.ToString();
    }

    private static string NormalizeManifestTagUris(string line, Uri manifestUri)
    {
        if (!line.Contains("URI=\"", StringComparison.Ordinal))
        {
            return line;
        }

        return Regex.Replace(line, "URI=\\\"([^\\\"]+)\\\"", match =>
        {
            var rawUri = match.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(rawUri) || Uri.TryCreate(rawUri, UriKind.Absolute, out _))
            {
                return match.Value;
            }

            var absoluteUri = new Uri(manifestUri, rawUri).ToString();
            return $"URI=\"{absoluteUri}\"";
        });
    }

    private void CleanupTemporaryManifest()
    {
        var tempPath = _tempManifestPath;
        _tempManifestPath = null;

        if (string.IsNullOrWhiteSpace(tempPath))
        {
            return;
        }

        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LinuxVideoPlayerService] Failed to remove temporary manifest: {Path}", tempPath);
        }
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

    private readonly record struct PreparedPlaybackSource(
        bool Success,
        string? PlaybackUrl,
        bool IsTemporaryManifest,
        bool DetectedManifest,
        bool IsLocalPath,
        string? ErrorMessage)
    {
        public static PreparedPlaybackSource Failure(string errorMessage) =>
            new(false, null, false, false, false, errorMessage);

        public static PreparedPlaybackSource CreateSuccess(string playbackUrl, bool isTemporaryManifest, bool detectedManifest, bool isLocalPath) =>
            new(true, playbackUrl, isTemporaryManifest, detectedManifest, isLocalPath, null);
    }

}

}



