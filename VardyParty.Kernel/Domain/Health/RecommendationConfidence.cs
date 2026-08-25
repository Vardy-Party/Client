using System.Text.Json.Serialization;

namespace VardyParty.Models;

[JsonConverter(typeof(JsonStringEnumConverter<RecommendationConfidence>))]
public enum RecommendationConfidence
{
    [JsonStringEnumMemberName("none")]
    None,
    [JsonStringEnumMemberName("low")]
    Low,
    [JsonStringEnumMemberName("medium")]
    Medium,
    [JsonStringEnumMemberName("high")]
    High
}
