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

        Task<string?> ResolveAsync(EnrichedStream current, CancellationToken cancellationToken)
        {
            if (current.Stream == null)
                return Task.FromResult<string?>(null);

            return api.ResolveM3U8ForPlaybackAsync(
                current.Stream,
                current.Referer ?? string.Empty,
                cancellationToken);
        }
    }
}
