namespace VardyParty.Kernel;

/// <summary>
/// Represents a stream that has been enriched with m3u8 resolution and metadata
/// </summary>
public class EnrichedStream
{
    /// <summary>
    /// The original stream from the API
    /// </summary>
    public required Stream Stream { get; set; }

    /// <summary>
    /// Current resolution status of the m3u8 URL
    /// </summary>
    public StreamResolutionStatus Status { get; set; } = StreamResolutionStatus.Pending;

    /// <summary>
    /// The resolved m3u8 URL (after calling GetM3U8UrlAsync on the API)
    /// Null until successfully resolved
    /// </summary>
    public string? ResolvedM3U8Url { get; set; }

    public string? Referer { get; set; }

    /// <summary>
    /// HTTP headers captured by Playwright when the m3u8 playlist was requested in-browser.
    /// </summary>
    public Dictionary<string, string>? RequestHeaders { get; set; }

    /// <summary>
    /// Stream health and metadata extracted from the m3u8 manifest
    /// Populated after m3u8 is resolved and tested
    /// </summary>
    public StreamHealth? Health { get; set; }

    /// <summary>
    /// Error message if resolution or testing failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Returns the quality label if metadata is available
    /// (e.g., "1080p 60fps 5000kbps")
    /// </summary>
    public string GetQualityDisplay()
    {
        if (Health?.Status == StreamHealthStatus.Healthy)
        {
            var label = Health.GetQualityLabel();
            return string.IsNullOrEmpty(label) ? "Unknown Quality" : label;
        }

        return Status switch
        {
            StreamResolutionStatus.Pending => "Loading...",
            StreamResolutionStatus.Resolved => "Testing...",
            StreamResolutionStatus.Healthy => Health?.GetQualityLabel() ?? "Unknown Quality",
            StreamResolutionStatus.Failed => $"Failed: {ErrorMessage}",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Returns whether this stream is ready for playback
    /// (m3u8 resolved AND health check passed)
    /// </summary>
    public bool IsReadyForPlayback => Status == StreamResolutionStatus.Healthy && !string.IsNullOrEmpty(ResolvedM3U8Url);
}

/// <summary>
/// Status of m3u8 URL resolution and health testing for a stream
/// </summary>
public enum StreamResolutionStatus
{
    /// <summary>
    /// Waiting to resolve the m3u8 URL
    /// </summary>
    Pending,

    /// <summary>
    /// M3U8 URL has been resolved but health test is pending
    /// </summary>
    Resolved,

    /// <summary>
    /// M3U8 URL resolved and health test passed
    /// </summary>
    Healthy,

    /// <summary>
    /// M3U8 resolution or health test failed
    /// </summary>
    Failed
}
