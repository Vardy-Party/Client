using System.Text.Json;
using System.Text.Json.Serialization;

namespace VardyParty.Kernel;

/// <summary>
/// Tolerant reputation converter. The games API does not emit stable casing
/// ("Very good" vs "Very Good"), and the strict <c>JsonStringEnumConverter</c>
/// threw <c>JsonException</c> on the mismatch, killing the whole streams payload.
/// Reads delegate to <see cref="StreamReputationParser.Parse"/>: case-insensitive,
/// whitespace-tolerant, and unknown/null/malformed values map to
/// <see cref="StreamReputation.None"/> — this converter never throws.
/// Writes emit the canonical wire label ("Very Good", "Good", "OK", "Poor", "Bad", "").
/// </summary>
public sealed class StreamReputationJsonConverter : JsonConverter<StreamReputation>
{
    // Opt in to Read being called for JSON null so it maps to None instead of
    // the framework throwing for a non-nullable value type.
    public override bool HandleNull => true;

    public override StreamReputation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return StreamReputationParser.Parse(reader.GetString());

            case JsonTokenType.Number:
                return reader.TryGetInt32(out var rank) && Enum.IsDefined(typeof(StreamReputation), rank)
                    ? (StreamReputation)rank
                    : StreamReputation.None;

            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return StreamReputation.None;

            default:
                // Null, true/false, or anything else unexpected.
                return StreamReputation.None;
        }
    }

    public override void Write(Utf8JsonWriter writer, StreamReputation value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(StreamReputationParser.ToDisplayLabel(value));
    }
}
