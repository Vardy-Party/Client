using AVFoundation;
using AVKit;
using CoreFoundation;
using CoreMedia;
using Foundation;
using ObjCRuntime;
using UIKit;
using VardyParty.Health;
using VardyParty.Models;
using VardyParty.Services;

namespace VardyParty.Platforms.MacCatalyst
{
    public class MacCatalystVideoPlayerService : INativeVideoPlayerService
    {
        public event EventHandler<bool>? BufferingStateChanged;

        private PlaybackMetrics? _currentMetrics;
        private AVPlayer? _currentPlayer;
        private AVPlayerItem? _currentPlayerItem;

        public PlaybackMetrics? GetCurrentMetrics()
        {
            return _currentMetrics;
        }

        public Task<PlaybackResult> PlayVideoAsync(
            string m3u8Url,
            string refererUrl,
            string title,
            Func<Task>? onNextStreamRequested = null,
            string? league = null,
            string? homeTeam = null,
            string? awayTeam = null,
            IReadOnlyDictionary<string, string>? requestHeaders = null)
        {
            var tcs = new TaskCompletionSource<PlaybackResult>();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Use a custom scheme to force AVAssetResourceLoaderDelegate to be called
                var uri = new Uri(m3u8Url);
                var scheme = uri.Scheme;
                var customScheme = scheme == "https" ? "fakehttps" : "fakehttp";
                var customUrl = m3u8Url.Replace(scheme + "://", customScheme + "://");

                var url = new NSUrl(customUrl);

                var headers = new NSMutableDictionary
                {
                    { new NSString("Referer"), new NSString(refererUrl) },
                    { new NSString("User-Agent"), new NSString("Mozilla/5.0 (Macintosh; Intel Mac OS X) AppleWebKit/605.1.15") }
                };

                var asset = new AVUrlAsset(url, (NSDictionary?)null);
                var loaderDelegate = new CustomResourceLoaderDelegate(refererUrl);
                asset.ResourceLoader.SetDelegate(loaderDelegate, DispatchQueue.MainQueue);

                var item = new AVPlayerItem(asset);
                var player = new AVPlayer(item);
                var controller = new AVPlayerViewController
                {
                    Player = player,
                    Title = title
                };

                // Store reference for metadata extraction
                _currentPlayer = player;
                _currentPlayerItem = item;

                bool metadataReported = false;
                IStreamHealthReporter? _healthReporter = null;

                // Resolve health reporter
                try
                {
                    _healthReporter = ((((VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(IStreamHealthReporter)) as IStreamHealthReporter))));
                }
                catch { }

                // Use KVO to detect when item is ready and extract metadata
                IDisposable? statusObserver = null;
                statusObserver = item.AddObserver(
                    new NSString("status"),
                    NSKeyValueObservingOptions.New,
                    change =>
                    {
                        if (item.Status == AVPlayerItemStatus.ReadyToPlay && !metadataReported)
                        {
                            metadataReported = true;
                            // Now extract real metadata from the player
                            ExtractVideoMetadata(item);
                        }
                    }
                );

                var window = UIApplication.SharedApplication.ConnectedScenes
                    .OfType<UIWindowScene>()
                    .SelectMany(s => s.Windows)
                    .FirstOrDefault(w => w.IsKeyWindow);
                var root = window?.RootViewController;
                if (root != null)
                {
                    root.PresentViewController(controller, true, () =>
                    {
                        player.Play();
                        statusObserver?.Dispose();
                    });
                    tcs.TrySetResult(PlaybackResult.SuccessResult("Playing via Mac Catalyst native player"));
                }
                else
                {
                    statusObserver?.Dispose();
                    tcs.TrySetResult(PlaybackResult.Completed("No active window for playback.", true));
                }
            });

            return tcs.Task;
        }

        private void ExtractVideoMetadata(AVPlayerItem? playerItem)
        {
            if (playerItem == null)
            {
                return;
            }

            try
            {
                var asset = playerItem.Asset;
                if (asset == null) return;

                var metrics = new PlaybackMetrics();

                // Extract video track information
                var videoTracks = asset.TracksWithMediaType(AVMediaTypes.Video.GetConstant()!);
                if (videoTracks != null && videoTracks.Length > 0)
                {
                    var videoTrack = videoTracks[0];

                    // Extract resolution from video track
                    var dimensions = videoTrack.NaturalSize;
                    if (dimensions.Width > 0 && dimensions.Height > 0)
                    {
                        metrics.Resolution = ((int)dimensions.Width, (int)dimensions.Height);
                    }

                    // Extract framerate
                    var framerate = (int)videoTrack.NominalFrameRate;
                    if (framerate > 0)
                    {
                        metrics.Framerate = framerate;
                    }

                    // Extract video codec from format descriptions
                    var formatDescriptions = videoTrack.FormatDescriptions;
                    if (formatDescriptions != null && formatDescriptions.Length > 0)
                    {
                        var formatDesc = formatDescriptions[0] as CMFormatDescription;
                        if (formatDesc != null)
                        {
                            var codecType = formatDesc.MediaSubType;
                            metrics.VideoCodec = CodecFourccToFriendlyName(codecType);
                        }
                    }
                }

                // Extract audio track information
                var audioTracks = asset.TracksWithMediaType(AVMediaTypes.Audio.GetConstant()!);
                if (audioTracks != null && audioTracks.Length > 0)
                {
                    var audioTrack = audioTracks[0];

                    // Extract audio codec from format descriptions
                    var formatDescriptions = audioTrack.FormatDescriptions;
                    if (formatDescriptions != null && formatDescriptions.Length > 0)
                    {
                        var formatDesc = formatDescriptions[0] as CMFormatDescription;
                        if (formatDesc != null)
                        {
                            var codecType = formatDesc.MediaSubType;
                            metrics.AudioCodec = CodecFourccToFriendlyName(codecType);
                        }
                    }
                }

                _currentMetrics = metrics;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MacCatalyst] Failed to extract video metadata: {ex.Message}");
            }
        }

        private void ReportMetadataIfReady(string m3u8Url, string refererUrl, IStreamHealthReporter? healthReporter, bool metadataReported)
        {
            // Report metadata if this is the first time we have format info
            if (!metadataReported && _currentMetrics?.Resolution.HasValue == true && healthReporter != null)
            {
                _ = healthReporter.ReportPlaybackStartedAsync(m3u8Url, refererUrl, metrics: _currentMetrics);
            }
        }

        private static string? CodecFourccToFriendlyName(uint fourcc)
        {
            // Common video codecs
            return fourcc switch
            {
                0x61637631 => "H.264",     // 'avc1'
                0x68657631 => "H.265",     // 'hev1'
                0x76703039 => "VP9",       // 'vp09'
                0x56503830 => "VP8",       // 'VP80'
                0x61763031 => "AV1",       // 'av01'

                // Common audio codecs
                0x6d703461 => "AAC",       // 'mp4a'
                0x616c6163 => "AAC-LC",    // 'alac'
                0x61632d33 => "AC-3",      // 'ac-3'
                0x65632d33 => "E-AC-3",    // 'ec-3'
                0x6f707573 => "Opus",      // 'opus'
                0x666c6163 => "FLAC",      // 'flac'
                0x2e6d7033 => "MP3",       // '.mp3'

                _ => null
            };
        }
    }

    public class CustomResourceLoaderDelegate : AVAssetResourceLoaderDelegate
    {
        private readonly string _refererUrl;
        private readonly NSUrlSession _session;

        public CustomResourceLoaderDelegate(string refererUrl)
        {
            _refererUrl = refererUrl;
            var config = NSUrlSessionConfiguration.DefaultSessionConfiguration;
            _session = NSUrlSession.FromConfiguration(config);
        }

        public override bool ShouldWaitForLoadingOfRequestedResource(AVAssetResourceLoader resourceLoader, AVAssetResourceLoadingRequest loadingRequest)
        {
            var reqUrl = loadingRequest?.Request?.Url;
            if (reqUrl == null)
            {
                try { loadingRequest?.FinishLoadingWithError(new NSError(new NSString("NSURLErrorDomain"), -1)); } catch { }
                return false;
            }
            var urlString = reqUrl.AbsoluteString;

            if (urlString.StartsWith("fakehttp"))
            {
                urlString = urlString.Replace("fakehttp", "http");
            }
            else if (urlString.StartsWith("fakehttps"))
            {
                urlString = urlString.Replace("fakehttps", "https");
            }

            var actualUrl = new NSUrl(urlString);
            var request = new NSMutableUrlRequest(actualUrl);
            request.HttpMethod = "GET";
            request.Headers = new NSDictionary(
                new NSString("Referer"), new NSString(_refererUrl),
                new NSString("User-Agent"), new NSString("Mozilla/5.0 (Macintosh; Intel Mac OS X) AppleWebKit/605.1.15")
            );

            var task = _session.CreateDataTask(request, (data, response, error) =>
            {
                if (error != null)
                {
                    loadingRequest.FinishLoadingWithError(error);
                    return;
                }

                if (response is NSHttpUrlResponse httpResponse)
                {
                    var contentType = httpResponse.MimeType;
                    if (urlString.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                    {
                        contentType = "video/MP2T";
                    }
                    else if (urlString.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                    {
                        contentType = "application/vnd.apple.mpegurl";
                    }

                    if (loadingRequest.ContentInformationRequest != null)
                    {
                        loadingRequest.ContentInformationRequest.ContentType = contentType;
                        loadingRequest.ContentInformationRequest.ContentLength = httpResponse.ExpectedContentLength;
                        loadingRequest.ContentInformationRequest.ByteRangeAccessSupported = true;
                    }

                    if (loadingRequest.DataRequest != null && data != null)
                    {
                        loadingRequest.DataRequest.Respond(data);
                    }

                    loadingRequest.FinishLoading();
                }
                else
                {
                    loadingRequest.FinishLoadingWithError(new NSError(new NSString("NSURLErrorDomain"), -1));
                }
            });

            task.Resume();
            return true;
        }
    }
}
