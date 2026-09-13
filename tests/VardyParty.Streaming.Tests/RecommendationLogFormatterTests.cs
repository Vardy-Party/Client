using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public class RecommendationLogFormatterTests
{
    [Fact]
    public void Format_Null_ReturnsNullMarker() =>
        Assert.Equal("(null)", RecommendationLogFormatter.Format(null));

    [Fact]
    public void Format_EmptyList_IncludesConfidenceAndCount()
    {
        var response = new RecommendationResponse
        {
            Confidence = RecommendationConfidence.High,
            HasData = true,
            GeneratedAt = 1789330000000,
            Recommended = []
        };

        Assert.Equal(
            "confidence=High, hasData=True, count=0, generatedAt=1789330000000",
            RecommendationLogFormatter.Format(response));
    }

    [Fact]
    public void Format_IncludesNameConfidenceUrlAndMeta()
    {
        var response = new RecommendationResponse
        {
            Confidence = RecommendationConfidence.High,
            HasData = true,
            Recommended =
            [
                new RecommendationItem
                {
                    StreamName = "SKA",
                    Url = "https://example.test/match",
                    Confidence = RecommendationConfidence.High,
                    Meta = new StreamMeta { Resolution = "1920x1080", Framerate = 50 }
                },
                new RecommendationItem
                {
                    StreamName = "Fubo US",
                    Url = "https://example.test/match",
                    Confidence = RecommendationConfidence.Medium
                }
            ]
        };

        var text = RecommendationLogFormatter.Format(response);

        Assert.Contains("confidence=High, hasData=True, count=2:", text);
        Assert.Contains("#1 SKA [High] https://example.test/match (1920x1080, 50fps)", text);
        Assert.Contains("#2 Fubo US [Medium] https://example.test/match", text);
    }

    [Fact]
    public void FormatTestOrder_IncludesIndexAndLabel()
    {
        var order = RecommendationLogFormatter.FormatTestOrder(
            [0, 2],
            index => index == 0 ? "SKA" : "DAZN IT");

        Assert.Equal("0:SKA, 2:DAZN IT", order);
    }
}
