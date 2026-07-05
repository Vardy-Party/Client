using VardyParty.Models;
using Xunit;

namespace VardyParty.Core.Tests;

public class StreamCatalogSourceTests
{
    [Fact]
    public void ResolveCatalogSource_TagsFootybitexUrlAsFbEvenWhenSourceSaysMp()
    {
        var stream = new Stream
        {
            Url = "https://www.footybitex.com/game/Argentina-vs-Cape-Verde/71210",
            Source = "mp",
            ResolutionStrategy = "v2"
        };

        Assert.Equal("fb", stream.ResolveCatalogSource());
        Assert.Equal("FB", stream.CatalogSourceBadgeLabel);
    }

    [Fact]
    public void ResolveCatalogSource_TagsMpoutqnUrlAsMp()
    {
        var stream = new Stream
        {
            Url = "https://jack09eo.mpoutqn4vebroad.my/football/fifa-world-cup-4374999/argentina-vs-cabo-verde.html",
            Source = "fb"
        };

        Assert.Equal("mp", stream.ResolveCatalogSource());
        Assert.Equal("V2", stream.CatalogSourceBadgeLabel);
    }

    [Fact]
    public void ResolveCatalogSource_UsesV2StrategyOnlyWhenUrlIsMpHost()
    {
        var stream = new Stream
        {
            Url = "https://live.example/stream",
            ResolutionStrategy = "v2",
            Source = "mp"
        };

        Assert.Equal("fb", stream.ResolveCatalogSource());
    }
}
