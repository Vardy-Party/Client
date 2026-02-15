#if IOS || MACCATALYST
using AVFoundation;
using AVKit;
using CoreFoundation;
using CoreMedia;
using Foundation;
using UIKit;
using VardyParty.Health;
using VardyParty.Models;
using VardyParty.Services;

namespace VardyParty.Platforms.iOS
{
    public class IosVideoPlayerService : INativeVideoPlayerService
    {
        public event EventHandler<bool>? BufferingStateChanged;

        private readonly HttpClient _httpClient;
        private PlaybackMetrics? _currentMetrics;

        public IosVideoPlayerService()
        {
            _httpClient = new HttpClient(new NSUrlSessionHandler())
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        public PlaybackMetrics? GetCurrentMetrics()
        {
            return _currentMetrics;
        }

        public Task<PlaybackResult> PlayVideoAsync(string m3u8Url, string refererUrl, string title, Func<Task>? onNextStreamRequested = null)
        {
            var tcs = new TaskCompletionSource<PlaybackResult>();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                var vc = GetTopViewController();
                if (vc == null)
                {
                    tcs.TrySetResult(PlaybackResult.Completed("No active view controller available.", true));
                    return;
                }

                var headers = new NSMutableDictionary();
                headers.Add(new NSString("Referer"), new NSString(refererUrl ?? string.Empty));
                headers.Add(new NSString("User-Agent"), new NSString("Mozilla/5.0 (iOS; MAUI) AppleWebKit/537.36"));

                var url = NSUrl.FromString(m3u8Url);
                if (url == null)
                {
                    tcs.TrySetResult(PlaybackResult.Completed("Invalid video URL.", true));
                    return;
                }

                var options = NSDictionary.FromObjectsAndKeys(
                    new NSObject[] { headers },
                    new NSObject[] { new NSString("AVURLAssetHTTPHeaderFieldsKey") });

                var asset = new AVUrlAsset(url, options);
                var loaderDelegate = new AllowAllResourceLoaderDelegate(_httpClient, refererUrl ?? string.Empty);
                var queue = new DispatchQueue("vp.loader");
                asset.ResourceLoader.SetDelegate(loaderDelegate, queue);

                var item = new AVPlayerItem(asset);
                var player = new AVPlayer(item);
                var playerVc = new AVPlayerViewController
                {
                    Player = player,
                    ShowsPlaybackControls = true,
                    ModalPresentationStyle = UIModalPresentationStyle.FullScreen,
                    Title = title
                };

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

                // Hook into buffering state
                NSNotificationCenter.DefaultCenter.AddObserver(AVPlayerItem.PlaybackStalledNotification, _ =>
                {
                    BufferingStateChanged?.Invoke(this, true);
                }, item);

                NSNotificationCenter.DefaultCenter.AddObserver(AVPlayerItem.TimeJumpedNotification, _ =>
                {
                    BufferingStateChanged?.Invoke(this, false);
                }, item);

                NSNotificationCenter.DefaultCenter.AddObserver(AVPlayerItem.DidPlayToEndTimeNotification, _ =>
                {
                    statusObserver.Dispose();
                    tcs.TrySetResult(PlaybackResult.Completed("Stream ended.", false));
                    playerVc.DismissViewController(true, null);
                }, item);

                NSNotificationCenter.DefaultCenter.AddObserver(AVPlayerItem.ItemFailedToPlayToEndTimeNotification, _ =>
                {
                    statusObserver.Dispose();
                    var errorMsg = item.Error?.LocalizedDescription ?? "Playback failed";
                    tcs.TrySetResult(PlaybackResult.Completed(errorMsg, true));
                    playerVc.DismissViewController(true, null);
                }, item);

                vc.PresentViewController(playerVc, true, () =>
                {
                    player.Play();
                    tcs.TrySetResult(PlaybackResult.SuccessResult("Playing via iOS native player"));
                });
            });

            return tcs.Task;
        }

        private static UIViewController? GetTopViewController()
        {
            var window = UIApplication.SharedApplication.ConnectedScenes
                .OfType<UIWindowScene>()
                .SelectMany(s => s.Windows)
                .FirstOrDefault(w => w.IsKeyWindow);

            var root = window?.RootViewController;
            if (root == null) return null;

            while (root.PresentedViewController is { } presented)
            {
                root = presented;
            }

            return root;
        }

        private sealed class AllowAllResourceLoaderDelegate : NSObject, IAVAssetResourceLoaderDelegate
        {
            private readonly HttpClient _httpClient;
            private readonly string _referer;

            public AllowAllResourceLoaderDelegate(HttpClient httpClient, string referer)
            {
                _httpClient = httpClient;
                _referer = referer;
            }

            [Export("resourceLoader:shouldWaitForLoadingOfRequestedResource:")]
            public bool ShouldWaitForLoadingOfRequestedResource(AVAssetResourceLoader resourceLoader, AVAssetResourceLoadingRequest loadingRequest)
            {
                _ = HandleRequestAsync(loadingRequest);
                return true;
            }

            private async Task HandleRequestAsync(AVAssetResourceLoadingRequest request)
            {
                try
                {
                    if (request.Request?.Url == null)
                    {
                        request.FinishLoadingWithError(new NSError(new NSString("vp.loader"), -1, null));
                        return;
                    }

                    using var message = new HttpRequestMessage(HttpMethod.Get, request.Request.Url.ToString());
                    message.Headers.TryAddWithoutValidation("Referer", _referer);
                    message.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (iOS; MAUI) AppleWebKit/537.36");

                    using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var url = request.Request.Url;
                    var isPlaylist = url.PathExtension?.Equals("m3u8", StringComparison.OrdinalIgnoreCase) == true;
                    var contentType = isPlaylist ? "application/vnd.apple.mpegurl" : "video/MP2T";

                    var info = request.ContentInformationRequest;
                    if (info != null)
                    {
                        info.ByteRangeAccessSupported = true;
                        info.ContentType = contentType;
                        if (response.Content.Headers.ContentLength.HasValue)
                        {
                            info.ContentLength = response.Content.Headers.ContentLength.Value;
                        }
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var dataRequest = request.DataRequest;
                    if (dataRequest != null)
                    {
                        var buffer = new byte[8192];
                        int read;
                        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
                        {
                            var chunk = new byte[read];
                            Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                            using var nsData = NSData.FromArray(chunk);
                            dataRequest.Respond(nsData);
                        }
                    }

                    request.FinishLoading();
                }
                catch (Exception ex)
                {
                    request.FinishLoadingWithError(new NSError(new NSString("vp.loader"), -1, new NSDictionary(new NSString("message"), new NSString(ex.Message))));
                }
            }
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
                System.Diagnostics.Debug.WriteLine($"[iOS] Failed to extract video metadata: {ex.Message}");
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
}
#endif
