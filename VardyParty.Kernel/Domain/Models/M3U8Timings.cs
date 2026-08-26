using System.Text.Json.Serialization;

namespace VardyParty.Kernel;

public class M3U8Timings
{
    [JsonPropertyName("start")]
    public long Start { get; set; }

    [JsonPropertyName("navigationComplete")]
    public long NavigationComplete { get; set; }

    [JsonPropertyName("buttonSearchStart")]
    public long ButtonSearchStart { get; set; }

    [JsonPropertyName("buttonFound")]
    public long ButtonFound { get; set; }

    [JsonPropertyName("clickComplete")]
    public long ClickComplete { get; set; }

    [JsonPropertyName("finalWaitComplete")]
    public long FinalWaitComplete { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }
}