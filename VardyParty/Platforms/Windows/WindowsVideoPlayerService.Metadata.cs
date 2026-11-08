using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Presentation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;

namespace VardyParty.Platforms.Windows
{
    public partial class WindowsVideoPlayerService
    {
        private void ExtractVideoMetadata(MediaPlaybackItem mediaItem, MediaPlayer player)
        {
            try
            {
                var metrics = new PlaybackMetrics();
                int? encodingKbps = null;

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

                    // Encoding bitrate is the last-resort source; typically 0 for HLS.
                    if (videoProps.Bitrate > 0)
                    {
                        encodingKbps = (int)(videoProps.Bitrate / 1000);
                        _logger.LogInformation("Bitrate from encoding properties: {BitrateKbps} kbps", encodingKbps);
                    }
                    else
                    {
                        _logger.LogInformation("Bitrate not in encoding properties (will use transfer meter / AdaptiveMediaSource)");
                    }
                }

                if (mediaItem.AudioTracks.Count > 0)
                {
                    _ = mediaItem.AudioTracks[0].GetEncodingProperties();
                }

                _currentMetrics = metrics;
                _encodingBitrateKbps = encodingKbps;
                UpdateBitrateFromAdaptiveSource(mediaItem);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract video metadata");
            }
        }

        /// <summary>
        /// Re-resolve <see cref="PlaybackMetrics.BitrateKbps"/> on every call so health samples
        /// follow the transfer meter's rolling average instead of keeping the startup window.
        /// </summary>
        private void UpdateBitrateFromAdaptiveSource(MediaPlaybackItem? mediaItem)
        {
            if (_currentMetrics == null)
                return;

            var ams = (mediaItem?.Source as MediaSource)?.AdaptiveMediaSource;
            _currentMetrics.BitrateKbps = ResolveBitrateKbps(ams, _activeTransferMeter);
            _logger.LogDebug("Resolved bitrate: {BitrateKbps} kbps", _currentMetrics.BitrateKbps);
        }

        /// <summary>The one bitrate both the video-info overlay and health report.</summary>
        internal int? ResolveBitrateKbps(AdaptiveMediaSource? ams, PlaybackTransferMeter? meter) =>
            MeasuredBitrate.ResolveKbps(
                _encodingBitrateKbps,
                ams?.CurrentDownloadBitrate ?? 0,
                meter?.BitrateKbps);


        private const string PlaybackUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

        private static void ApplyManagedPlaybackHeaders(
            HttpRequestMessage request,
            string refererUrl,
            IReadOnlyDictionary<string, string>? requestHeaders = null)
        {
            foreach (var (name, value) in ResolvePlaybackHeaders(refererUrl, requestHeaders))
                request.Headers.TryAddWithoutValidation(name, value);
        }

        private static void ApplyResourceByteRange(
            HttpRequestMessage request,
            AdaptiveMediaSourceDownloadRequestedEventArgs args)
        {
            if (args.ResourceByteRangeOffset is not ulong offset)
                return;

            var end = args.ResourceByteRangeLength is ulong length && length > 0
                ? offset + length - 1
                : (ulong?)null;
            request.Headers.Range = end is ulong last
                ? new RangeHeaderValue((long)offset, (long)last)
                : new RangeHeaderValue((long)offset, null);
        }

        private static string ResolvePlaybackMediaType(
            System.Net.Http.Headers.MediaTypeHeaderValue? contentType,
            string path,
            AdaptiveMediaSourceResourceType resourceType) =>
            PlaybackContentTypes.Resolve(
                contentType?.MediaType,
                path,
                ToPlaybackResourceKind(resourceType));

        private static PlaybackResourceKind ToPlaybackResourceKind(AdaptiveMediaSourceResourceType resourceType) =>
            resourceType switch
            {
                AdaptiveMediaSourceResourceType.Manifest => PlaybackResourceKind.Manifest,
                AdaptiveMediaSourceResourceType.MediaSegment => PlaybackResourceKind.MediaSegment,
                AdaptiveMediaSourceResourceType.InitializationSegment => PlaybackResourceKind.InitializationSegment,
                _ => PlaybackResourceKind.Other
            };

        /// <summary>
        /// Copy the body into a WinRT stream Media Foundation can read.
        /// Disposing <see cref="WindowsRuntimeStreamExtensions.AsStreamForWrite"/> closes the
        /// underlying stream, so the writer is detached before it is disposed and Seek can rewind.
        /// The caller roots the returned stream for the life of the adaptive source.
        /// </summary>
        private static async Task<InMemoryRandomAccessStream> CopyToRandomAccessStreamAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return await CopyToRandomAccessStreamAsync(bytes).ConfigureAwait(false);
        }

        private static async Task<InMemoryRandomAccessStream> CopyToRandomAccessStreamAsync(byte[] bytes)
        {
            var winrtStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(winrtStream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }

            winrtStream.Seek(0);
            return winrtStream;
        }

        private static int StatusCodeFromDownloadException(Exception ex)
        {
            if (ex is HttpRequestException { StatusCode: HttpStatusCode code })
                return (int)code;

            var msg = ex.Message ?? string.Empty;
            if (msg.Contains("404", StringComparison.Ordinal)) return 404;
            if (msg.Contains("502", StringComparison.Ordinal)) return 502;
            if (msg.Contains("503", StringComparison.Ordinal)) return 503;
            if (msg.Contains("403", StringComparison.Ordinal)) return 403;
            if (msg.Contains("401", StringComparison.Ordinal)) return 401;
            if (msg.Contains("500", StringComparison.Ordinal)) return 500;
            return 0;
        }

        private async Task<(AdaptiveMediaSourceCreationResult Result, InMemoryRandomAccessStream? ManifestStream)> CreateAdaptiveMediaSourceAsync(
            HttpClient http,
            Uri manifestUri,
            string refererUrl,
            IReadOnlyDictionary<string, string>? requestHeaders,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
            ApplyManagedPlaybackHeaders(request, refererUrl, requestHeaders);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var contentType = ResolvePlaybackMediaType(
                response.Content.Headers.ContentType,
                manifestUri.AbsolutePath,
                AdaptiveMediaSourceResourceType.Manifest);
            InMemoryRandomAccessStream? manifestStream = await CopyToRandomAccessStreamAsync(response, cancellationToken);
            try
            {
                var streamResult = await AdaptiveMediaSource.CreateFromStreamAsync(
                    manifestStream,
                    manifestUri,
                    contentType);
                if (streamResult.Status == AdaptiveMediaSourceCreationStatus.Success && streamResult.MediaSource != null)
                {
                    ConfigureAdaptiveLiveTolerance(streamResult.MediaSource);
                    var rooted = manifestStream;
                    manifestStream = null;
                    return (streamResult, rooted);
                }

                _logger.LogWarning(
                    "CreateFromStreamAsync returned {Status} for {Uri}",
                    streamResult.Status,
                    manifestUri);
                return (streamResult, null);
            }
            finally
            {
                if (manifestStream != null)
                {
                    try { manifestStream.Dispose(); } catch { }
                }
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

        /// <summary>
        /// Back off from the live edge so brief rebuffers are less likely to stall/fail
        /// (Android: larger LoadControl; Linux: --network-caching).
        /// </summary>
        private static void ConfigureAdaptiveLiveTolerance(AdaptiveMediaSource ams)
        {
            try
            {
                var desired = TimeSpan.FromSeconds(PlaybackPolicy.DesiredLiveOffsetSeconds);
                try
                {
                    var min = ams.MinLiveOffset;
                    if (min is TimeSpan minOffset && minOffset > TimeSpan.Zero && desired < minOffset)
                        desired = minOffset;
                }
                catch
                {
                    // MinLiveOffset may throw if not applicable yet.
                }

                ams.DesiredLiveOffset = desired;
            }
            catch
            {
                // DesiredLiveOffset is best-effort; some manifests reject the value.
            }
        }
    }
}
