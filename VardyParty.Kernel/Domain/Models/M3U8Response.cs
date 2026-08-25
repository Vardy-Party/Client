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
}