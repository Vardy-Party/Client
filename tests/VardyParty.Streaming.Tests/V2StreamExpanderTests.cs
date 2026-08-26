using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Streaming;
using VardyParty.TestSupport;

namespace VardyParty.Streaming.Tests;

public class V2StreamExpanderTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void Expand_NonV2Stream_ReturnsUnchanged()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/page")
            .With(s => s.Channel, "Channel North")
            .With(s => s.ResolutionStrategy, string.Empty)
            .Create();

        // Act
        var result = V2StreamExpander.Expand([stream]);

        // Assert
        Assert.Single(result);
        Assert.Same(stream, result[0]);
    }

    [Fact]
    public void Expand_V2WithPlayerStreams_CreatesOneCandidatePerLabel()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/match")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.StreamStatus, "ready")
            .With(s => s.PlayerStreams, new List<string> { "Channel East", "Channel North", "Channel West" })
            .Create();

        // Act
        var result = V2StreamExpander.Expand([stream]);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.All(result, s =>
        {
            Assert.Equal("v2", s.ResolutionStrategy);
            Assert.Equal(stream.Url, s.Url);
            Assert.False(string.IsNullOrWhiteSpace(s.PlayerStream));
            Assert.Equal(s.PlayerStream, s.Channel);
        });
        Assert.Equal(["Channel East", "Channel North", "Channel West"], result.Select(s => s.PlayerStream).ToList());
    }

    [Fact]
    public void Expand_V2WithEmptyPlayerStreams_DropsEntry()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/match")
            .With(s => s.Channel, "Channel South")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.PlayerStreams, new List<string>())
            .Create();

        // Act
        var result = V2StreamExpander.Expand([stream]);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void Expand_MixedFbAndEmptyMp_OnlyKeepsFb()
    {
        // Arrange
        var fb = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/a")
            .With(s => s.Channel, "Channel North")
            .With(s => s.ResolutionStrategy, "direct")
            .Create();
        var mp = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/match")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.PlayerStreams, new List<string>())
            .Create();

        // Act
        var result = V2StreamExpander.Expand([fb, mp]);

        // Assert
        Assert.Single(result);
        Assert.Same(fb, result[0]);
    }

    [Fact]
    public void Expand_V2DuplicateLabels_DeduplicatesCaseInsensitively()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/match")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.PlayerStreams, new List<string> { "Channel North", "channel north", "Channel West" })
            .Create();

        // Act
        var result = V2StreamExpander.Expand([stream]);

        // Assert
        Assert.Equal(2, result.Count);
    }
}
