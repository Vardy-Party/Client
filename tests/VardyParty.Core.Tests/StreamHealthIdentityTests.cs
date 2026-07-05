using VardyParty.Health;
using VardyParty.Models;
using Xunit;

namespace VardyParty.Core.Tests;

public class StreamHealthIdentityTests
{
    [Fact]
    public void GetStreamName_V1Stream_ReturnsNull()
    {
        var stream = new Stream
        {
            Url = "https://example.com/stream1",
            Channel = "Server A"
        };

        Assert.Null(StreamHealthIdentity.GetStreamName(stream));
    }

    [Fact]
    public void GetStreamName_V2Stream_ReturnsPlayerStreamLabel()
    {
        var stream = new Stream
        {
            Url = "https://example.com/match.html",
            ResolutionStrategy = "v2",
            PlayerStream = "TyR",
            StreamStatus = "ready"
        };

        Assert.Equal("TyR", StreamHealthIdentity.GetStreamName(stream));
    }

    [Fact]
    public void BuildStreamKey_V2_IncludesStreamName()
    {
        const string pageUrl = "https://example.com/match.html?x=1";

        var key = StreamHealthIdentity.BuildStreamKey(pageUrl, "TyR");

        Assert.Equal("https://example.com/match.html::TyR", key);
    }

    [Fact]
    public void BuildStreamKey_V1_UsesUrlOnly()
    {
        const string url = "https://example.com/stream1";

        var key = StreamHealthIdentity.BuildStreamKey(url, null);

        Assert.Equal("https://example.com/stream1", key);
    }

    [Fact]
    public void FromStream_V2_ReturnsUrlAndStreamName()
    {
        var stream = new Stream
        {
            Url = "https://example.com/match.html",
            ResolutionStrategy = "v2",
            PlayerStream = "Fola ID",
            StreamStatus = "ready"
        };

        var (streamUrl, streamName) = StreamHealthIdentity.FromStream(stream);

        Assert.Equal("https://example.com/match.html", streamUrl);
        Assert.Equal("Fola ID", streamName);
    }

    [Fact]
    public void MatchesRecommendation_RequiresLabel_WhenRecommendationIncludesStreamName()
    {
        var stream = new Stream
        {
            Url = "https://madplay.example/match",
            Channel = "Fubo US",
            PlayerStream = "Fubo US",
            ResolutionStrategy = "v2"
        };

        Assert.True(StreamHealthIdentity.MatchesRecommendation(
            stream,
            "https://madplay.example/match",
            "Fubo US"));
        Assert.False(StreamHealthIdentity.MatchesRecommendation(
            stream,
            "https://madplay.example/match",
            "TyR"));
    }
}
