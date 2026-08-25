using System.Text.Json.Serialization;

namespace VardyParty.Kernel;

/// <summary>
/// Catalog stream reputation. Integer values are the dedup rank (higher wins).
/// Wire names match the games API: "Very Good", "Good", "OK", "Poor", "Bad".
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StreamReputation>))]
public enum StreamReputation
{
    [JsonStringEnumMemberName("")]
    None = 0,

    [JsonStringEnumMemberName("Bad")]
    Bad = 1,

    [JsonStringEnumMemberName("Poor")]
    Poor = 2,

    [JsonStringEnumMemberName("OK")]
    Ok = 3,

    [JsonStringEnumMemberName("Good")]
    Good = 4,

    [JsonStringEnumMemberName("Very Good")]
    VeryGood = 5
}

public static class StreamReputationParser
{
    public static StreamReputation Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return StreamReputation.None;

        var trimmed = value.Trim();
        if (trimmed.Equals("Very Good", StringComparison.OrdinalIgnoreCase))
            return StreamReputation.VeryGood;
        if (trimmed.Equals("Good", StringComparison.OrdinalIgnoreCase))
            return StreamReputation.Good;
        if (trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase))
            return StreamReputation.Ok;
        if (trimmed.Equals("Poor", StringComparison.OrdinalIgnoreCase))
            return StreamReputation.Poor;
        if (trimmed.Equals("Bad", StringComparison.OrdinalIgnoreCase))
            return StreamReputation.Bad;

        return StreamReputation.None;
    }
}
