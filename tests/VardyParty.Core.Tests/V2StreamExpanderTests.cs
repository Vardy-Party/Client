using System.Linq;
using VardyParty.Models;
using VardyParty.Resolvers;
using Xunit;

namespace VardyParty.Core.Tests;

public class V2StreamExpanderTests
{
    [Fact]
    public void Expand_NonV2Stream_ReturnsUnchanged()
    {
        var stream = new Stream { Url = "https://example.com/page", Channel = "HD" };

        var result = V2StreamExpander.Expand([stream]);

        Assert.Single(result);
        Assert.Same(stream, result[0]);
    }

    [Fact]
    public void Expand_V2WithPlayerStreams_CreatesOneCandidatePerLabel()
    {
        var stream = new Stream
        {
            Url = "https://madplay.example/match",
            ResolutionStrategy = "v2",
            StreamStatus = "ready",
            PlayerStreams = ["Fola ID", "Fubo US", "Peacock"]
        };

        var result = V2StreamExpander.Expand([stream]);

        Assert.Equal(3, result.Count);
        Assert.All(result, s =>
        {
            Assert.Equal("v2", s.ResolutionStrategy);
            Assert.Equal(stream.Url, s.Url);
            Assert.False(string.IsNullOrWhiteSpace(s.PlayerStream));
            Assert.Equal(s.PlayerStream, s.Channel);
        });
        Assert.Equal(["Fola ID", "Fubo US", "Peacock"], result.Select(s => s.PlayerStream).ToList());
    }

    [Fact]
    public void Expand_V2WithEmptyPlayerStreams_KeepsSingleEntry()
    {
        var stream = new Stream
        {
            Url = "https://madplay.example/match",
            Channel = "Fallback",
            ResolutionStrategy = "v2",
            PlayerStreams = []
        };

        var result = V2StreamExpander.Expand([stream]);

        Assert.Single(result);
        Assert.Same(stream, result[0]);
    }

    [Fact]
    public void Expand_V2DuplicateLabels_DeduplicatesCaseInsensitively()
    {
        var stream = new Stream
        {
            Url = "https://madplay.example/match",
            ResolutionStrategy = "v2",
            PlayerStreams = ["Fubo US", "fubo us", "Peacock"]
        };

        var result = V2StreamExpander.Expand([stream]);

        Assert.Equal(2, result.Count);
    }
}
