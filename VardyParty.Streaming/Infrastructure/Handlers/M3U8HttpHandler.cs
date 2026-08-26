using Microsoft.Extensions.Logging;

namespace VardyParty.Streaming;

public class M3U8HttpHandler(ILogger<M3U8HttpHandler> logger) : DelegatingHandler
{
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
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("*/*");

            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            logger.LogInformation("M3U8 Handler - Request to: {Uri}", request.RequestUri);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
