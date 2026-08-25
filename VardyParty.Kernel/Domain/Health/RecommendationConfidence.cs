using System.Text.Json;
using System.Text.Json.Serialization;

namespace VardyParty.Models;

[JsonConverter(typeof(RecommendationConfidenceJsonConverter))]
public enum RecommendationConfidence
{
    None,
    Low,
    Medium,
    High
}

public sealed class RecommendationConfidenceJsonConverter : JsonConverter<RecommendationConfidence>
{
    public override RecommendationConfidence Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return RecommendationConfidence.None;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            return RecommendationConfidence.None;
        }

        return Parse(reader.GetString());
    }

    public override void Write(
        Utf8JsonWriter writer,
        RecommendationConfidence value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }

    public static RecommendationConfidence Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RecommendationConfidence.None;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out RecommendationConfidence parsed)
               && Enum.IsDefined(parsed)
            ? parsed
            : RecommendationConfidence.None;
    }
}
