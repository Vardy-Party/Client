using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using VardyParty.Models;
using VardyParty.Resolvers;
using Xunit;

namespace VardyParty.Core.Tests;

public class StreamCatalogSourceOrdererTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void OrderFbBeforeMp_PlacesFbStreamsAheadOfMp()
    {
        // Arrange
        var streams = new List<Stream>
        {
            _fixture.Build<Stream>()
                .With(s => s.Url, string.Empty)
                .With(s => s.Channel, "Channel East")
                .With(s => s.Source, "mp")
                .With(s => s.ResolutionStrategy, "v2")
                .Create(),
            _fixture.Build<Stream>()
                .With(s => s.Url, "https://streams.example.com/watch/1")
                .With(s => s.Channel, "Channel North")
                .With(s => s.Source, "fb")
                .With(s => s.ResolutionStrategy, string.Empty)
                .Create(),
            _fixture.Build<Stream>()
                .With(s => s.Url, string.Empty)
                .With(s => s.Channel, "Channel West")
                .With(s => s.Source, "mp")
                .With(s => s.ResolutionStrategy, "v2")
                .Create(),
            _fixture.Build<Stream>()
                .With(s => s.Url, "https://streams.example.com/watch/2")
                .With(s => s.Channel, "Channel South")
                .With(s => s.Source, "fb")
                .With(s => s.ResolutionStrategy, string.Empty)
                .Create()
        };

        // Act
        var ordered = StreamCatalogSourceOrderer.OrderFbBeforeMp(streams);

        // Assert
        Assert.Equal(["Channel North", "Channel South", "Channel East", "Channel West"], ordered.Select(s => s.Channel).ToList());
    }

    [Fact]
    public void OrderFbBeforeMp_PreservesRelativeOrderWithinSource()
    {
        // Arrange
        var streams = new List<Stream>
        {
            _fixture.Build<Stream>()
                .With(s => s.Url, "https://streams.example.com/a")
                .With(s => s.Channel, "Channel North")
                .With(s => s.Source, "fb")
                .With(s => s.ResolutionStrategy, string.Empty)
                .Create(),
            _fixture.Build<Stream>()
                .With(s => s.Url, string.Empty)
                .With(s => s.Channel, "Channel East")
                .With(s => s.Source, "mp")
                .With(s => s.ResolutionStrategy, "v2")
                .Create(),
            _fixture.Build<Stream>()
                .With(s => s.Url, "https://streams.example.com/b")
                .With(s => s.Channel, "Channel South")
                .With(s => s.Source, "fb")
                .With(s => s.ResolutionStrategy, string.Empty)
                .Create(),
            _fixture.Build<Stream>()
                .With(s => s.Url, string.Empty)
                .With(s => s.Channel, "Channel West")
                .With(s => s.Source, "mp")
                .With(s => s.ResolutionStrategy, "v2")
                .Create()
        };

        // Act
        var ordered = StreamCatalogSourceOrderer.OrderFbBeforeMp(streams);

        // Assert
        Assert.Equal(["Channel North", "Channel South", "Channel East", "Channel West"], ordered.Select(s => s.Channel).ToList());
    }

    [Fact]
    public void OrderIndexesFbBeforeMp_PartitionsRecommendedOrderBySource()
    {
        // Arrange
        var streams = new List<Stream>
        {
            _fixture.Build<Stream>()
                .With(s => s.Url, string.Empty)
                .With(s => s.Channel, "Channel East")
                .With(s => s.Source, "mp")
                .With(s => s.ResolutionStrategy, "v2")
                .Create(),
            _fixture.Build<Stream>()
                .With(s => s.Url, "https://streams.example.com/1")
                .With(s => s.Channel, "Channel North")
                .With(s => s.Source, "fb")
                .With(s => s.ResolutionStrategy, string.Empty)
                .Create(),
            _fixture.Build<Stream>()
                .With(s => s.Url, string.Empty)
                .With(s => s.Channel, "Channel West")
                .With(s => s.Source, "mp")
                .With(s => s.ResolutionStrategy, "v2")
                .Create()
        };

        // Act
        var orderedIndexes = StreamCatalogSourceOrderer.OrderIndexesFbBeforeMp(
            [0, 1, 2],
            index => streams[index]);

        // Assert
        Assert.Equal([1, 0, 2], orderedIndexes);
    }
}
