using Microsoft.Extensions.Logging;

namespace VardyParty.Streaming;

public class M3U8HttpHandler(ILogger<M3U8HttpHandler> logger) : DelegatingHandler
{
    private string? _referer;

    public void SetReferer(string referer)
    {
        _referer = referer;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Only apply custom headers to M3U8 and TS segment requests
        if (request.RequestUri != null &&
            (request.RequestUri.AbsolutePath.EndsWith(".m3u8") ||
             request.RequestUri.AbsolutePath.EndsWith(".ts") ||
             request.RequestUri.AbsolutePath.EndsWith(".txt")))
        {
            // Add Accept header
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("*/*");

            // Add Referer header if set
            if (!string.IsNullOrEmpty(_referer))
            {
                request.Headers.Referrer = new Uri(_referer);
            }

            // Add User-Agent header
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            logger.LogInformation("M3U8 Handler - Request to: {Uri}", request.RequestUri);
            logger.LogInformation("M3U8 Handler - Referer: {Referer}", _referer);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
