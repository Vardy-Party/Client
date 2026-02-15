using System.Text.Json.Serialization;

namespace VardyParty.Models;

public class M3U8Response
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
    
    [JsonPropertyName("timings")]
    public M3U8Timings? Timings { get; set; }
}