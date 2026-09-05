using VardyParty.Kernel;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Resolves stream m3u8 URLs incrementally and extracts metadata
/// Yields results progressively as they complete, allowing UI to update in real-time
/// </summary>
public interface IStreamResolver
{
    /// <summary>
    /// Incrementally resolves m3u8 URLs for a list of streams
    /// Keeps up to <paramref name="batchSize"/> resolves in flight
    /// Yields each enriched stream as soon as it completes (does not wait for the rest of the window)
    /// </summary>
    /// <param name="streams">Deduplicated streams from the API (without m3u8 metadata yet)</param>
    /// <param name="batchSize">Maximum number of streams to resolve in parallel (default: 3)</param>
    /// <param name="cancellationToken">Cancellation token for stopping resolution</param>
    /// <param name="onTotalStreamsKnown">Callback to report the total stream count before testing begins</param>
    /// <returns>Async enumerable that yields enriched streams as they complete</returns>
    IAsyncEnumerable<EnrichedStream> ResolveStreamsIncrementallyAsync(
        List<StreamModel> streams,
        int batchSize = 3,
        CancellationToken cancellationToken = default,
        Action<int>? onTotalStreamsKnown = null);

    /// <summary>
    /// Resolves m3u8 URL for a single stream
    /// Used when user selects a stream to play (m3u8 URLs are single-use)
    /// </summary>
    /// <param name="stream">The stream to resolve m3u8 for</param>
    /// <param name="refererUrl">Referer URL for m3u8 request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The resolved m3u8 URL, or null if resolution failed</returns>
    Task<string?> ResolveM3U8UrlAsync(
        StreamModel stream,
        string refererUrl,
        CancellationToken cancellationToken = default);
}
