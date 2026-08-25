namespace VardyParty.Health;

public class StreamHealth
{
    public StreamHealthStatus Status { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public long CheckDurationMs { get; set; }

    // Stream metadata extracted from EXT-X-STREAM-INF tags
    /// <summary>
    /// Resolution of the stream (e.g., "1920x1080", "1280x720", etc.)
    /// Extracted from RESOLUTION attribute in m3u8 manifest
    /// </summary>
    public string? Resolution { get; set; }

    /// <summary>
    /// Frame rate in frames per second (e.g., 25, 30, 60)
    /// Extracted from FRAME-RATE attribute in m3u8 manifest
    /// Stored as integer for easier sorting and comparison
    /// </summary>
    public int? FrameRate { get; set; }

    /// <summary>
    /// Bitrate in kilobits per second (kbps)
    /// Extracted from BANDWIDTH attribute in m3u8 manifest (divided by 1000)
    /// Stored as integer for easier sorting and comparison
    /// </summary>
    public int? Bitrate { get; set; }
    /// <summary>
    /// Video codec identifier (e.g., avc1.640028)
    /// Parsed from CODECS attribute in EXT-X-STREAM-INF or from media segments.
    /// </summary>
    public string? VideoCodec { get; set; }

    /// <summary>
    /// Audio codec identifier (e.g., mp4a.40.2)
    /// Parsed from CODECS attribute in EXT-X-STREAM-INF or from media segments.
    /// </summary>
    public string? AudioCodec { get; set; }

    /// <summary>
    /// Human-readable quality string for UI display (e.g., "1080p 60fps 5000kbps")
    /// Derived from Resolution, FrameRate, and Bitrate
    /// </summary>
    public string GetQualityLabel()
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(Resolution))
        {
            // Extract height from resolution like "1920x1080" -> "1080p"
            var resolutionParts = Resolution.Split('x');
            if (resolutionParts.Length == 2 && int.TryParse(resolutionParts[1], out var height))
            {
                parts.Add($"{height}p");
            }
            else
            {
                parts.Add(Resolution);
            }
        }

        if (FrameRate.HasValue)
        {
            parts.Add($"{FrameRate}fps");
        }

        if (Bitrate.HasValue)
        {
            parts.Add($"{Bitrate}kbps");
        }

        return string.Join(" ", parts) ?? "Unknown Quality";
    }
}
