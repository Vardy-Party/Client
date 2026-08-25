#if IOS
using AVFoundation;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using VardyParty.Playback;
using VardyParty.Ports;

namespace VardyParty
{
    public class IosVideoPlayerService : AppleVideoPlayerServiceBase
    {
        private readonly HttpClient _httpClient;
        private AllowAllResourceLoaderDelegate? _loaderDelegate;

        public IosVideoPlayerService(
            ILogger<IosVideoPlayerService> logger,
            IStreamSwitchingService switching,
            ResolveFreshPlaybackUrlAsync resolveFresh,
            IStreamHealthReporter healthReporter)
            : base(logger, switching, resolveFresh, healthReporter)
        {
            _httpClient = new HttpClient(new NSUrlSessionHandler())
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        protected override AVUrlAsset CreateAsset(
            string m3u8Url,
            string referer,
            IReadOnlyDictionary<string, string>? requestHeaders)
        {
            var headers = new NSMutableDictionary();
            headers.Add(new NSString("Referer"), new NSString(referer ?? string.Empty));
            headers.Add(new NSString("User-Agent"), new NSString("Mozilla/5.0 (iOS; MAUI) AppleWebKit/537.36"));
            if (requestHeaders != null)
            {
                foreach (var pair in requestHeaders)
                    headers[new NSString(pair.Key)] = new NSString(pair.Value ?? string.Empty);
            }

            var url = NSUrl.FromString(m3u8Url)
                ?? throw new InvalidOperationException("Invalid video URL.");

            var options = NSDictionary.FromObjectsAndKeys(
                new NSObject[] { headers },
                new NSObject[] { new NSString("AVURLAssetHTTPHeaderFieldsKey") });

            var asset = new AVUrlAsset(url, options);
            _loaderDelegate = new AllowAllResourceLoaderDelegate(_httpClient, referer ?? string.Empty);
            asset.ResourceLoader.SetDelegate(_loaderDelegate, new DispatchQueue("vp.loader"));
            return asset;
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
            public bool ShouldWaitForLoadingOfRequestedResource(
                AVAssetResourceLoader resourceLoader,
                AVAssetResourceLoadingRequest loadingRequest)
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

                    using var response = await _httpClient
                        .SendAsync(message, HttpCompletionOption.ResponseHeadersRead)
                        .ConfigureAwait(false);
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
                            info.ContentLength = response.Content.Headers.ContentLength.Value;
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
                    request.FinishLoadingWithError(new NSError(
                        new NSString("vp.loader"),
                        -1,
                        new NSDictionary(new NSString("message"), new NSString(ex.Message))));
                }
            }
        }
    }
}
#endif
