using System.Text.Json.Serialization;

namespace VardyParty.Models;

public class StreamHealthReport
{
    [JsonPropertyName("streamUrl")]
    public string StreamUrl { get; set; } = string.Empty;

    /// <summary>
    /// MadPlay / v2 player label when multiple streams share the same page URL.
    /// </summary>
    [JsonPropertyName("streamName")]
    public string? StreamName { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty; // "working" | "failed" | "buffering" | "unknown"

    [JsonPropertyName("quality")]
    public string? Quality { get; set; } // "excellent" | "good" | "poor"

    [JsonPropertyName("bitrate")]
    public int? Bitrate { get; set; }

    [JsonPropertyName("buffering")]
    public bool? Buffering { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; } // e.g., "1920x1080" - sent once on first working report

    [JsonPropertyName("framerate")]
    public int? Framerate { get; set; } // e.g., 30, 60 - sent once on first working report

    [JsonPropertyName("videoCodec")]
    public string? VideoCodec { get; set; } // e.g., "H.264", "H.265" - sent once on first working report

    [JsonPropertyName("audioCodec")]
    public string? AudioCodec { get; set; } // e.g., "AAC", "AC-3" - sent once on first working report

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;
}

public class RecommendationResponse
{
    [JsonPropertyName("recommended")]
    public List<RecommendationItem> Recommended { get; set; } = new();

    [JsonPropertyName("hasData")]
    public bool HasData { get; set; }

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = string.Empty; // "high" | "medium" | "low" | "none"
}

public class RecommendationItem
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("streamName")]
    public string? StreamName { get; set; }

    /// <summary>
    /// Per-stream freshness: high, medium, low, or none. Playback tries
    /// high-confidence recommended streams before low-confidence ones.
    /// </summary>
    [JsonPropertyName("confidence")]
    public string? Confidence { get; set; }

    [JsonPropertyName("meta")]
    public StreamMeta? Meta { get; set; }
}

public class StreamMeta
{
    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; } // e.g., "1920x1080"

    [JsonPropertyName("framerate")]
    public int? Framerate { get; set; } // e.g., 30, 60

    [JsonPropertyName("videoCodec")]
    public string? VideoCodec { get; set; } // e.g., "H.264", "H.265"

    [JsonPropertyName("audioCodec")]
    public string? AudioCodec { get; set; } // e.g., "AAC", "AC-3"

    [JsonPropertyName("bitrate")]
    public int? Bitrate { get; set; } // kbps

    [JsonPropertyName("lastMetaReportTime")]
    public long? LastMetaReportTime { get; set; } // timestamp when metadata was last reported
}

public class StreamStatsResponse
{
    [JsonPropertyName("streams")]
    public List<StreamStats> Streams { get; set; } = new();
}

public class StreamStats
{
    [JsonPropertyName("streamUrl")]
    public string StreamUrl { get; set; } = string.Empty;

    [JsonPropertyName("streamName")]
    public string? StreamName { get; set; }

    [JsonPropertyName("successCount")]
    public int SuccessCount { get; set; }

    [JsonPropertyName("failureCount")]
    public int FailureCount { get; set; }

    [JsonPropertyName("successRate")]
    public double SuccessRate { get; set; }

    [JsonPropertyName("lastSuccess")]
    public long? LastSuccess { get; set; }

    [JsonPropertyName("lastFailure")]
    public long? LastFailure { get; set; }

    [JsonPropertyName("avgQuality")]
    public double AvgQuality { get; set; }

    [JsonPropertyName("avgBitrate")]
    public double AvgBitrate { get; set; }

    [JsonPropertyName("activeViewers")]
    public int ActiveViewers { get; set; }

    [JsonPropertyName("lastReportTime")]
    public long LastReportTime { get; set; }
}
