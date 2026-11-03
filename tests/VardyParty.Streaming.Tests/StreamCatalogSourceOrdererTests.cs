using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Streaming;
using VardyParty.TestSupport;

namespace VardyParty.Streaming.Tests;

public class StreamCatalogSourceOrdererTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void OrderMpBeforeFb_PreservesInputOrder_Fcfs()
    {
        var streams = new List<Stream>
        {
            _fixture.Build<Stream>()
                .With(s => s.Url, "https://streams.example.com/watch/1")
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
                .With(s => s.Url, "https://streams.example.com/watch/2")
                .With(s => s.Channel, "Channel South")
                .With(s => s.Source, "fb")
                .With(s => s.ResolutionStrategy, string.Empty)
                .Create(),
        };

        var ordered = StreamCatalogSourceOrderer.OrderMpBeforeFb(streams);

        Assert.Equal(["Channel North", "Channel East", "Channel South"], ordered.Select(s => s.Channel).ToList());
    }

    [Fact]
    public void OrderIndexesMpBeforeFb_PreservesIndexOrder_Fcfs()
    {
        var streams = new List<Stream>
        {
            _fixture.Build<Stream>()
                .With(s => s.Url, "https://streams.example.com/1")
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
                .With(s => s.Url, string.Empty)
                .With(s => s.Channel, "Channel West")
                .With(s => s.Source, "mp")
                .With(s => s.ResolutionStrategy, "v2")
                .Create()
        };

        var orderedIndexes = StreamCatalogSourceOrderer.OrderIndexesMpBeforeFb(
            [0, 1, 2],
            index => streams[index]);

        Assert.Equal([0, 1, 2], orderedIndexes);
    }
}
