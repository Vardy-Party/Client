using AutoFixture;
using VardyParty.Kernel;
using Xunit;
using VardyParty.TestSupport;

namespace VardyParty.Kernel.Tests;

public class StreamCatalogSourceTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void ResolveCatalogSource_TagsSourceMpAsMpEvenWhenUrlIsNotMpHost()
    {
        // Arrange — slim FCTV/v2 rows use source=mp + resolutionStrategy=v2 on non-MP page URLs
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/game/home-united-vs-away-city/71210")
            .With(s => s.Source, "mp")
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
    public void ResolveCatalogSource_UsesSourceAndStrategyNotUrlHost()
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
        Assert.Equal("mp", source);
    }

    [Fact]
    public void ResolveCatalogSource_TagsExplicitFbSourceAsFb()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/game/home-vs-away/1")
            .With(s => s.Source, "fb")
            .With(s => s.ResolutionStrategy, "v1")
            .Create();

        // Act
        var source = stream.ResolveCatalogSource();
        var badge = stream.CatalogSourceBadgeLabel;

        // Assert
        Assert.Equal("fb", source);
        Assert.Equal("FB", badge);
    }
}
