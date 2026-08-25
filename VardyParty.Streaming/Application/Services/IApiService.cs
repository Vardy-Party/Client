using VardyParty.Models;

namespace VardyParty.Streaming;

public interface IApiService
{
    Task<StreamResponse?> GetStreamsAsync(string league, string homeTeam, string awayTeam, bool forceRefresh = false);
    Task<M3U8Response?> GetM3U8UrlAsync(string streamUrl);
    Task<M3U8Response?> GetM3U8UrlAsync(string streamUrl, string? playerStreamName);

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
