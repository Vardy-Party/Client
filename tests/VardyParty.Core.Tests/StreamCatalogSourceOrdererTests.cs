using System.Collections.Generic;
using System.Linq;
using VardyParty.Models;
using VardyParty.Resolvers;
using Xunit;

namespace VardyParty.Core.Tests;

public class StreamCatalogSourceOrdererTests
{
    [Fact]
    public void OrderFbBeforeMp_PlacesFbStreamsAheadOfMp()
    {
        var streams = new List<Stream>
        {
            new() { Url = "https://mpoutqn.example/watch/1", Channel = "MP HD", Source = "mp", ResolutionStrategy = "v2" },
            new() { Url = "https://footybitex.example/watch/1", Channel = "FB HD", Source = "fb" },
            new() { Url = "https://mpoutqn.example/watch/2", Channel = "MP SD", Source = "mp", ResolutionStrategy = "v2" },
            new() { Url = "https://footybitex.example/watch/2", Channel = "FB SD", Source = "fb" }
        };

        var ordered = StreamCatalogSourceOrderer.OrderFbBeforeMp(streams);

        Assert.Equal(["FB HD", "FB SD", "MP HD", "MP SD"], ordered.Select(s => s.Channel).ToList());
    }

    [Fact]
    public void OrderFbBeforeMp_PreservesRelativeOrderWithinSource()
    {
        var streams = new List<Stream>
        {
            new() { Url = "https://footybitex.example/a", Channel = "FB A", Source = "fb" },
            new() { Url = "https://mpoutqn.example/a", Channel = "MP A", Source = "mp", ResolutionStrategy = "v2" },
            new() { Url = "https://footybitex.example/b", Channel = "FB B", Source = "fb" },
            new() { Url = "https://mpoutqn.example/b", Channel = "MP B", Source = "mp", ResolutionStrategy = "v2" }
        };

        var ordered = StreamCatalogSourceOrderer.OrderFbBeforeMp(streams);

        Assert.Equal(["FB A", "FB B", "MP A", "MP B"], ordered.Select(s => s.Channel).ToList());
    }

    [Fact]
    public void OrderIndexesFbBeforeMp_PartitionsRecommendedOrderBySource()
    {
        var streams = new List<Stream>
        {
            new() { Url = "https://mpoutqn.example/1", Channel = "MP 1", Source = "mp", ResolutionStrategy = "v2" },
            new() { Url = "https://footybitex.example/1", Channel = "FB 1", Source = "fb" },
            new() { Url = "https://mpoutqn.example/2", Channel = "MP 2", Source = "mp", ResolutionStrategy = "v2" }
        };

        var orderedIndexes = StreamCatalogSourceOrderer.OrderIndexesFbBeforeMp(
            [0, 1, 2],
            index => streams[index]);

        Assert.Equal([1, 0, 2], orderedIndexes);
    }
}
