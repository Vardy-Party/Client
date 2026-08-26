using AutoFixture;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Streaming;
using VardyParty.TestSupport;

namespace VardyParty.Streaming.Tests;

public class StreamHealthIdentityTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void GetStreamName_V1Stream_ReturnsNull()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/stream1")
            .With(s => s.Channel, "Channel North")
            .With(s => s.ResolutionStrategy, string.Empty)
            .With(s => s.PlayerStream, string.Empty)
            .With(s => s.StreamStatus, string.Empty)
            .Create();

        // Act
        var name = StreamHealthIdentity.GetStreamName(stream);

        // Assert
        Assert.Null(name);
    }

    [Fact]
    public void GetStreamName_V2Stream_ReturnsPlayerStreamLabel()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/match.html")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.PlayerStream, "Channel East")
            .With(s => s.StreamStatus, "ready")
            .Create();

        // Act
        var name = StreamHealthIdentity.GetStreamName(stream);

        // Assert
        Assert.Equal("Channel East", name);
    }

    [Fact]
    public void BuildStreamKey_V2_IncludesStreamName()
    {
        // Arrange
        const string pageUrl = "https://streams.example.com/match.html?x=1";

        // Act
        var key = StreamHealthIdentity.BuildStreamKey(pageUrl, "Channel East");

        // Assert
        Assert.Equal("https://streams.example.com/match.html::Channel East", key);
    }

    [Fact]
    public void BuildStreamKey_V1_UsesUrlOnly()
    {
        // Arrange
        const string url = "https://streams.example.com/stream1";

        // Act
        var key = StreamHealthIdentity.BuildStreamKey(url, null);

        // Assert
        Assert.Equal("https://streams.example.com/stream1", key);
    }

    [Fact]
    public void FromStream_V2_ReturnsUrlAndStreamName()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/match.html")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.PlayerStream, "Channel West")
            .With(s => s.StreamStatus, "ready")
            .Create();

        // Act
        var (streamUrl, streamName) = StreamHealthIdentity.FromStream(stream);

        // Assert
        Assert.Equal("https://streams.example.com/match.html", streamUrl);
        Assert.Equal("Channel West", streamName);
    }

    [Fact]
    public void ResolveReportUrl_PrefersCatalogPageOverM3U8()
    {
        // Arrange
        const string manifest = "https://cdn.example.com/live/playlist.m3u8?token=abc";
        const string page = "https://streams.example.com/match.html";

        // Act
        var fromPair = StreamHealthIdentity.ResolveReportUrl(manifest, page);
        var pageOnly = StreamHealthIdentity.ResolveReportUrl(page, null);
        var manifestOnly = StreamHealthIdentity.ResolveReportUrl(manifest, null);

        // Assert
        Assert.Equal(page, fromPair);
        Assert.Equal(page, pageOnly);
        Assert.Equal(manifest, manifestOnly);
    }

    [Fact]
    public void MatchesRecommendation_RequiresLabel_WhenRecommendationIncludesStreamName()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/match")
            .With(s => s.Channel, "Channel North")
            .With(s => s.PlayerStream, "Channel North")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.StreamStatus, string.Empty)
            .Create();

        // Act
        var matchesNorth = StreamHealthIdentity.MatchesRecommendation(
            stream,
            "https://streams.example.com/match",
            "Channel North");
        var matchesEast = StreamHealthIdentity.MatchesRecommendation(
            stream,
            "https://streams.example.com/match",
            "Channel East");

        // Assert
        Assert.True(matchesNorth);
        Assert.False(matchesEast);
    }
}
