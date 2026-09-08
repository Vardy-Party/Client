using AutoFixture;
using VardyParty.Kernel;
using Xunit;
using VardyParty.TestSupport;

namespace VardyParty.Kernel.Tests;

public class StreamCatalogSourceTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void ResolveCatalogSource_TagsNonMpHostUrlAsFbEvenWhenSourceSaysMp()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/game/home-united-vs-away-city/71210")
            .With(s => s.Source, "mp")
            .With(s => s.ResolutionStrategy, "v2")
            .Create();

        // Act
        var source = stream.ResolveCatalogSource();
        var badge = stream.CatalogSourceBadgeLabel;

        // Assert
        Assert.Equal("fb", source);
        Assert.Equal("FB", badge);
    }

    [Fact]
    public void ResolveCatalogSource_TagsV2StrategyAsMpWhenUrlIsEmpty()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, string.Empty)
            .With(s => s.Source, "fb")
            .With(s => s.ResolutionStrategy, "v2")
            .Create();

        // Act
        var source = stream.ResolveCatalogSource();
        var badge = stream.CatalogSourceBadgeLabel;

        // Assert
        Assert.Equal("mp", source);
        Assert.Equal("V2", badge);
    }

    [Fact]
    public void ResolveCatalogSource_UsesV2StrategyOnlyWhenUrlIsMpHost()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/stream")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.Source, "mp")
            .Create();

        // Act
        var source = stream.ResolveCatalogSource();

        // Assert
        Assert.Equal("fb", source);
    }

    [Fact]
    public void ResolveCatalogSource_TagsFctvMatchPageAsMp()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://www.fctv33hd.example/football/league-match-12345/home-united-vs-away-city.html")
            .With(s => s.Source, "mp")
            .With(s => s.ResolutionStrategy, "v2")
            .Create();

        // Act
        var source = stream.ResolveCatalogSource();

        // Assert
        Assert.Equal("mp", source);
        Assert.Equal("V2", stream.CatalogSourceBadgeLabel);
    }

    [Theory]
    [InlineData("https://jack12.mp.example/player", "mp", "V2")]
    [InlineData("https://jack23eo.mpgreatest.example/player", "mp", "V2")]
    [InlineData("https://cdn.mpgreatest.example/watch", "mp", "V2")]
    [InlineData("https://mpoutqn.example.com/northgate", "mp", "V2")]
    [InlineData("https://streams.example.com/blackjack/clip.mp4", "fb", "FB")]
    [InlineData("https://streams.example.com/game?ref=jackpot.mp", "fb", "FB")]
    public void ResolveCatalogSource_UsesHostNotPath(string url, string expectedSource, string expectedBadge)
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, url)
            .With(s => s.Source, "mp")
            .With(s => s.ResolutionStrategy, "v2")
            .Create();

        // Act
        var source = stream.ResolveCatalogSource();

        // Assert
        Assert.Equal(expectedSource, source);
        Assert.Equal(expectedBadge, stream.CatalogSourceBadgeLabel);
    }
}
