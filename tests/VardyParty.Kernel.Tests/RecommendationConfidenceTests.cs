using System.Text.Json;
using VardyParty.Models;
using Xunit;

namespace VardyParty.Tests;

public class RecommendationConfidenceTests
{
    [Theory]
    [InlineData("high", RecommendationConfidence.High)]
    [InlineData("medium", RecommendationConfidence.Medium)]
    [InlineData("low", RecommendationConfidence.Low)]
    [InlineData("none", RecommendationConfidence.None)]
    public void JsonStringEnumConverter_ReadsApiStrings(
        string value,
        RecommendationConfidence expected)
    {
        // Arrange
        var json = $"\"{value}\"";

        // Act
        var deserialized = JsonSerializer.Deserialize<RecommendationConfidence>(json);

        // Assert
        Assert.Equal(expected, deserialized);
    }

    [Fact]
    public void JsonStringEnumConverter_WritesApiMemberNames()
    {
        // Arrange
        const RecommendationConfidence confidence = RecommendationConfidence.High;

        // Act
        var json = JsonSerializer.Serialize(confidence);

        // Assert
        Assert.Equal("\"high\"", json);
    }
}
