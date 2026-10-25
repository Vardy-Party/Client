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
            var response = await api.GetM3U8UrlAsync(
                current.Stream.Url,
                EnrichedStreamM3U8Apply.PlayerStreamNameForPlayback(current.Stream),
                current.Stream.ResolutionStrategy,
                current.Stream.Source,
                cancellationToken);

            if (response is null || string.IsNullOrWhiteSpace(response.Url))
                return null;

            EnrichedStreamM3U8Apply.Apply(current, response);
            return response.Url;
        }
    }
}
