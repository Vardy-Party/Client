using System.Text.Json.Serialization;

namespace VardyParty.Kernel;

public class M3U8Response
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("requestHeaders")]
    public Dictionary<string, string>? RequestHeaders { get; set; }

    [JsonPropertyName("timings")]
    public M3U8Timings? Timings { get; set; }

    /// <summary>
    /// Media URLs the MP player actually fetched (rewritten hosts / decoy extensions).
    /// Playlist-relative segments are not playable from HttpClient.
    /// </summary>
    [JsonPropertyName("rewrittenSegments")]
    public List<string>? RewrittenSegments { get; set; }

    [JsonPropertyName("selectedStream")]
    public string? SelectedStream { get; set; }

    [JsonPropertyName("streams")]
    public List<string>? Streams { get; set; }
}