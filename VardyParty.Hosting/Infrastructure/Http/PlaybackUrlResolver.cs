using System.Linq;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Streaming;

namespace VardyParty.Hosting;

/// <summary>
/// Adapts <see cref="IApiService"/> onto Playback's resolve delegate so OS players
/// never take a catalog HTTP client.
/// </summary>
public static class PlaybackUrlResolver
{
    public static ResolveFreshPlaybackUrlAsync Bind(IApiService api)
    {
        ArgumentNullException.ThrowIfNull(api);
        return ResolveAsync;

        async Task<string?> ResolveAsync(EnrichedStream current, CancellationToken cancellationToken)
        {
            if (current.Stream == null)
                return null;

            // FB /play tokens are one-shot — always go through GetM3U8UrlAsync and
            // refresh Referer/headers on the pool row for the new URL.
            var playerStreamName = current.Stream.RequiresV2StreamSelection
                && !current.Stream.IsCountdown
                && !string.IsNullOrWhiteSpace(current.Stream.PlayerStream)
                    ? current.Stream.PlayerStream.Trim()
                    : null;

            var response = await api.GetM3U8UrlAsync(
                current.Stream.Url,
                playerStreamName,
                current.Stream.ResolutionStrategy,
                current.Stream.Source);

            if (response is null || string.IsNullOrWhiteSpace(response.Url))
                return null;

            current.ResolvedM3U8Url = response.Url;
            current.RequestHeaders = response.RequestHeaders;
            current.RewrittenSegments = response.RewrittenSegments?
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .ToList();
            if (response.RequestHeaders is { Count: > 0 })
            {
                foreach (var pair in response.RequestHeaders)
                {
                    if (pair.Key.Equals("referer", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        current.Referer = pair.Value;
                        break;
                    }
                }
            }

            return response.Url;
        }
    }
}
