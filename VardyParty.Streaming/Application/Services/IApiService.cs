using VardyParty.Models;

namespace VardyParty.Services;

public interface IApiService : IGamesCatalogApi
{
    Task<StreamResponse?> GetStreamsAsync(string league, string homeTeam, string awayTeam, bool forceRefresh = false);
    Task<M3U8Response?> GetM3U8UrlAsync(string streamUrl);
    Task<M3U8Response?> GetM3U8UrlAsync(string streamUrl, string? playerStreamName);

    /// <summary>
    /// Gets enriched streams with incremental m3u8 resolution and metadata
    /// Returns an async enumerable that yields streams as they complete testing
    /// </summary>
    /// <param name="league">League name</param>
    /// <param name="homeTeam">Home team name</param>
    /// <param name="awayTeam">Away team name</param>
    /// <param name="onTotalStreamsKnown">Callback invoked when total stream count is known (before testing begins)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of enriched streams with progressive metadata</returns>
    IAsyncEnumerable<EnrichedStream> GetEnrichedStreamsAsync(
        string league,
        string homeTeam,
        string awayTeam,
        Action<int>? onTotalStreamsKnown = null,
        System.Threading.CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves m3u8 URL for a single stream for playback
    /// (m3u8 URLs are single-use, so they need to be resolved again when user selects stream)
    /// </summary>
    /// <param name="stream">The stream to resolve m3u8 for</param>
    /// <param name="refererUrl">Referer URL for the m3u8 request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The resolved m3u8 URL, or null if resolution failed</returns>
    Task<string?> ResolveM3U8ForPlaybackAsync(
        Models.Stream stream,
        string refererUrl,
        System.Threading.CancellationToken cancellationToken = default);
}
