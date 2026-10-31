using System.Threading;
using VardyParty.Kernel;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

public interface IApiService
{
    Task<StreamResponse?> GetStreamsAsync(string league, string homeTeam, string awayTeam, bool forceRefresh = false);
    /// <summary>
    /// Resolves a page to its m3u8 via the local service. <c>null</c> means the
    /// service answered with no playlist (the stream's verdict). Throws
    /// <see cref="LocalServiceUnavailableException"/> when the service gave no
    /// verdict (unreachable, our timeout, its own 5xx, MP disabled, 401/403):
    /// callers must retry or defer, never mark the stream Failed or report it.
    /// </summary>
    Task<M3U8Response?> GetM3U8UrlAsync(string streamUrl, CancellationToken cancellationToken = default);
    Task<M3U8Response?> GetM3U8UrlAsync(string streamUrl, string? playerStreamName, CancellationToken cancellationToken = default);
    Task<M3U8Response?> GetM3U8UrlAsync(
        string streamUrl,
        string? playerStreamName,
        string? resolutionStrategy,
        string? source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves m3u8 URL for a single stream for playback
    /// (m3u8 URLs are single-use, so they need to be resolved again when user selects stream)
    /// </summary>
    /// <param name="stream">The stream to resolve m3u8 for</param>
    /// <param name="refererUrl">Referer URL for the m3u8 request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The resolved m3u8 URL, or null if the service answered with no playlist</returns>
    /// <exception cref="LocalServiceUnavailableException">The local service gave no verdict.</exception>
    Task<string?> ResolveM3U8ForPlaybackAsync(
        StreamModel stream,
        string refererUrl,
        System.Threading.CancellationToken cancellationToken = default);
}
