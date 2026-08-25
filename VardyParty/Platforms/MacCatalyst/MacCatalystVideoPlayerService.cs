#if MACCATALYST
using AVFoundation;
using CoreFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using VardyParty.Platforms.Apple;
using VardyParty.Ports;
using VardyParty.Streaming;

namespace VardyParty.Platforms.MacCatalyst
{
    public class MacCatalystVideoPlayerService : AppleVideoPlayerServiceBase
    {
        private CustomResourceLoaderDelegate? _loaderDelegate;

        public MacCatalystVideoPlayerService(
            ILogger<MacCatalystVideoPlayerService> logger,
            IStreamSwitchingService switching,
            IApiService api,
            IStreamHealthReporter healthReporter)
            : base(logger, switching, api, healthReporter)
        {
        }

        protected override AVUrlAsset CreateAsset(
            string m3u8Url,
            string referer,
            IReadOnlyDictionary<string, string>? requestHeaders)
        {
            var uri = new Uri(m3u8Url);
            var scheme = uri.Scheme;
            var customScheme = scheme == "https" ? "fakehttps" : "fakehttp";
            var customUrl = m3u8Url.Replace(scheme + "://", customScheme + "://");
            var url = new NSUrl(customUrl);
            var asset = new AVUrlAsset(url, (NSDictionary?)null);
            _loaderDelegate = new CustomResourceLoaderDelegate(referer);
            asset.ResourceLoader.SetDelegate(_loaderDelegate, DispatchQueue.MainQueue);
            return asset;
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

        public override bool ShouldWaitForLoadingOfRequestedResource(
            AVAssetResourceLoader resourceLoader,
            AVAssetResourceLoadingRequest loadingRequest)
        {
            var reqUrl = loadingRequest?.Request?.Url;
            if (reqUrl == null)
            {
                try { loadingRequest?.FinishLoadingWithError(new NSError(new NSString("NSURLErrorDomain"), -1)); }
                catch { }
                return false;
            }

            var urlString = reqUrl.AbsoluteString;
            if (urlString.StartsWith("fakehttp"))
                urlString = urlString.Replace("fakehttp", "http");
            else if (urlString.StartsWith("fakehttps"))
                urlString = urlString.Replace("fakehttps", "https");

            var actualUrl = new NSUrl(urlString);
            var request = new NSMutableUrlRequest(actualUrl)
            {
                HttpMethod = "GET",
                Headers = new NSDictionary(
                    new NSString("Referer"), new NSString(_refererUrl),
                    new NSString("User-Agent"),
                    new NSString("Mozilla/5.0 (Macintosh; Intel Mac OS X) AppleWebKit/605.1.15"))
            };

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
                        contentType = "video/MP2T";
                    else if (urlString.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                        contentType = "application/vnd.apple.mpegurl";

                    if (loadingRequest.ContentInformationRequest != null)
                    {
                        loadingRequest.ContentInformationRequest.ContentType = contentType;
                        loadingRequest.ContentInformationRequest.ContentLength = httpResponse.ExpectedContentLength;
                        loadingRequest.ContentInformationRequest.ByteRangeAccessSupported = true;
                    }

                    if (loadingRequest.DataRequest != null && data != null)
                        loadingRequest.DataRequest.Respond(data);

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
#endif
