using System.Text.Json;
using VardyParty.Models;
using Xunit;

namespace VardyParty.Tests;

public class RecommendationConfidenceTests
{
    [Theory]
    [InlineData("high", RecommendationConfidence.High)]
    [InlineData("MEDIUM", RecommendationConfidence.Medium)]
    [InlineData("low", RecommendationConfidence.Low)]
    [InlineData("none", RecommendationConfidence.None)]
    [InlineData("", RecommendationConfidence.None)]
    [InlineData("nope", RecommendationConfidence.None)]
    public void Parse_MapsApiStringsCaseInsensitively(string value, RecommendationConfidence expected)
    {
        // Arrange
        var json = $"\"{value}\"";

        // Act
        var parsed = RecommendationConfidenceJsonConverter.Parse(value);
        var deserialized = JsonSerializer.Deserialize<RecommendationConfidence>(json);

        // Assert
        Assert.Equal(expected, parsed);
        Assert.Equal(expected, deserialized);
    }

    [Fact]
    public void Parse_Null_IsNone()
    {
        // Arrange
        const string? missing = null;

        // Act
        var parsed = RecommendationConfidenceJsonConverter.Parse(missing);
        var deserialized = JsonSerializer.Deserialize<RecommendationConfidence>("null");

        // Assert
        Assert.Equal(RecommendationConfidence.None, parsed);
        Assert.Equal(RecommendationConfidence.None, deserialized);
    }
}
