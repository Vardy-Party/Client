using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using HttpClientWin = Windows.Web.Http.HttpClient;

namespace VardyParty.Platforms.Windows
{
    public partial class WindowsVideoPlayerService
    {
        private void ExtractVideoMetadata(MediaPlaybackItem mediaItem, MediaPlayer player)
        {
            try
            {
                var metrics = new PlaybackMetrics();

                // Get actual playback resolution from the session (not encoding properties)
                // For HLS adaptive streams, encoding properties return max variant, not actual playing resolution
                uint actualWidth = 0;
                uint actualHeight = 0;

                try
                {
                    // Get from the media player's playback session for accurate current resolution
                    if (player?.PlaybackSession != null)
                    {
                        actualWidth = player.PlaybackSession.NaturalVideoWidth;
                        actualHeight = player.PlaybackSession.NaturalVideoHeight;
                    }
                }
                catch (Exception ex)
                {
                    LogIgnored("ReadPlaybackSessionSize", ex);
                }

                if (mediaItem.VideoTracks.Count > 0)
                {
                    var videoTrack = mediaItem.VideoTracks[0];
                    var videoProps = videoTrack.GetEncodingProperties();

                    // Extract resolution - prefer PlaybackSession over encoding properties for HLS
                    if (actualWidth > 0 && actualHeight > 0)
                    {
                        metrics.Resolution = ((int)actualWidth, (int)actualHeight);
                        _logger.LogInformation("Resolution from PlaybackSession: {Width}x{Height}", actualWidth, actualHeight);
                    }
                    else if (videoProps.Width > 0 && videoProps.Height > 0)
                    {
                        metrics.Resolution = ((int)videoProps.Width, (int)videoProps.Height);
                        _logger.LogInformation(
                            "Resolution from encoding properties: {Width}x{Height} (may be max variant, not actual)",
                            videoProps.Width,
                            videoProps.Height);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Resolution not available (Session: {SessionWidth}x{SessionHeight}, Props: {PropWidth}x{PropHeight})",
                            actualWidth,
                            actualHeight,
                            videoProps.Width,
                            videoProps.Height);
                    }

                    // Extract framerate - often unavailable for HLS streams
                    if (videoProps.FrameRate.Numerator > 0 && videoProps.FrameRate.Denominator > 0)
                    {
                        var fps = (int)(videoProps.FrameRate.Numerator / (double)videoProps.FrameRate.Denominator);
                        if (fps > 0)
                        {
                            metrics.Framerate = fps;
                            _logger.LogInformation(
                                "Framerate: {Fps} fps ({Numerator}/{Denominator})",
                                fps,
                                videoProps.FrameRate.Numerator,
                                videoProps.FrameRate.Denominator);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "Framerate calculation resulted in 0 ({Numerator}/{Denominator})",
                                videoProps.FrameRate.Numerator,
                                videoProps.FrameRate.Denominator);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Framerate not available from encoding properties (expected for HLS adaptive streams)");
                    }

                    // Extract bitrate - typically 0 for HLS (use AdaptiveMediaSource instead)
                    if (videoProps.Bitrate > 0)
                    {
                        metrics.BitrateKbps = (int)(videoProps.Bitrate / 1000);
                        _logger.LogInformation("Bitrate from encoding properties: {BitrateKbps} kbps", metrics.BitrateKbps);
                    }
                    else
                    {
                        _logger.LogInformation("Bitrate not in encoding properties (will use AdaptiveMediaSource.CurrentDownloadBitrate)");
                    }
                }

                if (mediaItem.AudioTracks.Count > 0)
                {
                    _ = mediaItem.AudioTracks[0].GetEncodingProperties();
                }

                _currentMetrics = metrics;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract video metadata");
            }
        }

        private void UpdateBitrateFromAdaptiveSource(MediaPlaybackItem? mediaItem)
        {
            // For HLS/DASH streams, bitrate comes from AdaptiveMediaSource during playback, not encoding properties
            if (mediaItem?.Source is MediaSource ms && ms.AdaptiveMediaSource != null && _currentMetrics != null)
            {
                var ams = ms.AdaptiveMediaSource;
                if (ams.CurrentDownloadBitrate > 0)
                {
                    _currentMetrics.BitrateKbps = (int)(ams.CurrentDownloadBitrate / 1000);
                    _logger.LogInformation("Bitrate from AdaptiveMediaSource: {BitrateKbps} kbps", _currentMetrics.BitrateKbps);
                }
                else
                {
                    _logger.LogInformation(
                        "AdaptiveMediaSource.CurrentDownloadBitrate not available yet: {Bitrate}",
                        ams.CurrentDownloadBitrate);
                }
            }
            else
            {
                var hasMediaItem = mediaItem != null;
                var hasMediaSource = mediaItem?.Source != null;
                var hasAMS = mediaItem?.Source is MediaSource msCheck && msCheck.AdaptiveMediaSource != null;
                var hasMetrics = _currentMetrics != null;
                _logger.LogInformation(
                    "Cannot update bitrate - mediaItem={HasMediaItem}, MediaSource={HasMediaSource}, AMS={HasAms}, metrics={HasMetrics}",
                    hasMediaItem,
                    hasMediaSource,
                    hasAMS,
                    hasMetrics);
            }
        }

        private const string PlaybackUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

        private static void ConfigurePlaybackHttpClient(
            HttpClientWin client,
            string refererUrl,
            IReadOnlyDictionary<string, string>? requestHeaders = null)
        {
            foreach (var (name, value) in ResolvePlaybackHeaders(refererUrl, requestHeaders))
            {
                client.DefaultRequestHeaders.TryAppendWithoutValidation(name, value);
            }
        }

        private static void ApplyPlaybackRequestHeaders(
            global::Windows.Web.Http.HttpRequestMessage request,
            string refererUrl,
            IReadOnlyDictionary<string, string>? requestHeaders = null)
        {
            foreach (var (name, value) in ResolvePlaybackHeaders(refererUrl, requestHeaders))
            {
                request.Headers.TryAppendWithoutValidation(name, value);
            }
        }

        private static IEnumerable<KeyValuePair<string, string>> ResolvePlaybackHeaders(
            string refererUrl,
            IReadOnlyDictionary<string, string>? requestHeaders)
        {
            if (requestHeaders is { Count: > 0 })
            {
                foreach (var pair in requestHeaders)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Value))
                    {
                        yield return pair;
                    }
                }

                yield break;
            }

            yield return new KeyValuePair<string, string>("User-Agent", PlaybackUserAgent);
            if (string.IsNullOrWhiteSpace(refererUrl))
            {
                yield break;
            }

            yield return new KeyValuePair<string, string>("Referer", refererUrl);
            if (Uri.TryCreate(refererUrl, UriKind.Absolute, out var refererUri))
            {
                yield return new KeyValuePair<string, string>(
                    "Origin",
                    $"{refererUri.Scheme}://{refererUri.Authority}");
            }
        }

        private async Task<AdaptiveMediaSourceCreationResult> CreateAdaptiveMediaSourceAsync(
            HttpClientWin client,
            Uri manifestUri)
        {
            var adaptiveResult = await AdaptiveMediaSource.CreateFromUriAsync(manifestUri, client);
            if (adaptiveResult.Status == AdaptiveMediaSourceCreationStatus.Success && adaptiveResult.MediaSource != null)
            {
                return adaptiveResult;
            }

            _logger.LogWarning(
                "CreateFromUriAsync returned {Status}; downloading manifest with Referer/Origin",
                adaptiveResult.Status);

            try
            {
                var response = await client.GetAsync(manifestUri);
                response.EnsureSuccessStatusCode();
                var manifestStream = await response.Content.ReadAsInputStreamAsync();
                return await AdaptiveMediaSource.CreateFromStreamAsync(
                    manifestStream,
                    manifestUri,
                    "application/vnd.apple.mpegurl");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual manifest download failed");
                return adaptiveResult;
            }
        }
    }
}
