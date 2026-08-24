using AutoFixture;
using VardyParty.Models;
using Xunit;

namespace VardyParty.Core.Tests;

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
}
