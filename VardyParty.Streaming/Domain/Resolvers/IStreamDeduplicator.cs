using VardyParty.Kernel;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Deduplicates streams by their base m3u8 URL, keeping only the best variant per unique URL
/// </summary>
public interface IStreamDeduplicator
{
    /// <summary>
    /// Deduplicates a list of streams by removing duplicates with the same base m3u8 URL (ignoring query parameters).
    /// When multiple streams share the same base URL, keeps the one with the highest reputation.
    /// </summary>
    /// <param name="streams">The list of streams to deduplicate</param>
    /// <returns>List of deduplicated streams with one entry per unique base URL</returns>
    List<StreamModel> DeduplicateStreams(List<StreamModel> streams);

    /// <summary>
    /// Extracts the base URL (everything before the query string) from a full stream URL
    /// </summary>
    /// <param name="url">The full stream URL including potential query parameters</param>
    /// <returns>The base URL without query parameters</returns>
    string ExtractBaseUrl(string url);
}
