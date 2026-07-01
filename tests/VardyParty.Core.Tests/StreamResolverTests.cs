using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VardyParty.Health;
using VardyParty.Models;
using VardyParty.Resolvers;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests;

public class StreamResolverTests
{
    private readonly Mock<IStreamHealthChecker> _healthCheckerMock = new();
    private readonly Mock<ILocalLanPlayService> _localLanPlayServiceMock = new();

    private StreamResolver Sut => new(
        _healthCheckerMock.Object,
        _localLanPlayServiceMock.Object,
        NullLogger<StreamResolver>.Instance);

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_EmptyList_YieldsNothing()
    {
        var results = new List<EnrichedStream>();
        await foreach (var stream in Sut.ResolveStreamsIncrementallyAsync([]))
        {
            results.Add(stream);
        }

        Assert.Empty(results);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_SingleStream_ResolvesAndTests()
    {
        // Arrange
        var stream = new Stream { Url = "http://stream.com/1", Channel = "Channel1" };
        // ReSharper disable once InconsistentNaming
        const string m3u8Url = "http://m3u8.com/playlist.m3u8?token=abc";

        _localLanPlayServiceMock
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new M3U8Response { Url = m3u8Url });

        _healthCheckerMock
            .Setup(h => h.CheckStreamHealthAsync(m3u8Url, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StreamHealth
            {
                Status = StreamHealthStatus.Healthy,
                Url = m3u8Url,
                Resolution = "1920x1080",
                FrameRate = 30,
                Bitrate = 5000
            });

        // Act
        var results = new List<EnrichedStream>();
        await foreach (var enriched in Sut.ResolveStreamsIncrementallyAsync([stream]))
        {
            results.Add(enriched);
        }

        // Assert
        Assert.Single(results);
        Assert.Equal(stream.Channel, results[0].Stream.Channel);
        Assert.Equal(m3u8Url, results[0].ResolvedM3U8Url);
        Assert.Equal(StreamResolutionStatus.Healthy, results[0].Status);
        Assert.NotNull(results[0].Health);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_MultipleStreams_ProcessesInBatches()
    {
        // Arrange
        var streams = Enumerable.Range(1, 5)
            .Select(i => new Stream { Url = $"http://stream.com/{i}", Channel = $"Channel{i}" })
            .ToList();

        _localLanPlayServiceMock
            .Setup(s => s.ResolveM3U8UrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new M3U8Response { Url = "http://m3u8.com/playlist.m3u8" });

        _healthCheckerMock
            .Setup(h => h.CheckStreamHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StreamHealth { Status = StreamHealthStatus.Healthy });

        // Act
        var results = new List<EnrichedStream>();
        await foreach (var enriched in Sut.ResolveStreamsIncrementallyAsync(streams, 2))
        {
            results.Add(enriched);
        }

        // Assert
        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal(StreamResolutionStatus.Healthy, r.Status));
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_FailedM3U8Resolution_MarksFailed()
    {
        // Arrange
        var stream = new Stream { Url = "http://stream.com/1", Channel = "Channel1" };

        _localLanPlayServiceMock
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((M3U8Response?)null);

        // Act
        var results = new List<EnrichedStream>();
        await foreach (var enriched in Sut.ResolveStreamsIncrementallyAsync([stream]))
        {
            results.Add(enriched);
        }

        // Assert
        Assert.Single(results);
        Assert.Equal(StreamResolutionStatus.Failed, results[0].Status);
        Assert.NotNull(results[0].ErrorMessage);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_FailedHealthCheck_MarksFailed()
    {
        // Arrange
        var stream = new Stream { Url = "http://stream.com/1", Channel = "Channel1" };
        // ReSharper disable once InconsistentNaming
        const string m3u8Url = "http://m3u8.com/playlist.m3u8";

        _localLanPlayServiceMock
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new M3U8Response { Url = m3u8Url });

        _healthCheckerMock
            .Setup(h => h.CheckStreamHealthAsync(m3u8Url, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StreamHealth { Status = StreamHealthStatus.SegmentUnreachable });

        // Act
        var results = new List<EnrichedStream>();
        await foreach (var enriched in Sut.ResolveStreamsIncrementallyAsync([stream]))
        {
            results.Add(enriched);
        }

        // Assert
        Assert.Single(results);
        Assert.Equal(StreamResolutionStatus.Failed, results[0].Status);
    }

    [Fact]
    public async Task ResolveM3U8UrlAsync_ResolutionFails_ReturnsNull()
    {
        // Arrange
        var stream = new Stream { Url = "http://stream.com/1", Channel = "Channel1" };

        _localLanPlayServiceMock
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((M3U8Response?)null);

        // Act
        var result = await Sut.ResolveM3U8UrlAsync(stream, "http://example.com");

        // Assert: Returns null because no valid configuration/HTTP response
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveM3U8UrlAsync_ResolutionSucceeds_ReturnsUrl()
    {
        // Arrange
        var stream = new Stream { Url = "http://stream.com/1", Channel = "Channel1" };
        // ReSharper disable once InconsistentNaming
        const string m3u8Url = "http://m3u8.com/playlist.m3u8";

        _localLanPlayServiceMock
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new M3U8Response { Url = m3u8Url });

        // Act
        var result = await Sut.ResolveM3U8UrlAsync(stream, "http://example.com");

        // Assert
        Assert.Equal(m3u8Url, result);
    }

    [Fact]
    public void EnrichedStream_IsReadyForPlayback_ReturnsTrueWhenHealthy()
    {
        var enriched = new EnrichedStream
        {
            Stream = new Stream { Url = "http://example.com/stream", Channel = "Test" },
            Status = StreamResolutionStatus.Healthy,
            ResolvedM3U8Url = "http://example.com/playlist.m3u8",
            Health = new StreamHealth { Status = StreamHealthStatus.Healthy }
        };

        Assert.True(enriched.IsReadyForPlayback);
    }

    [Fact]
    public void EnrichedStream_GetQualityDisplay_ShowsQualityWhenHealthy()
    {
        var enriched = new EnrichedStream
        {
            Stream = new Stream { Channel = "Test" },
            Status = StreamResolutionStatus.Healthy,
            Health = new StreamHealth
            {
                Status = StreamHealthStatus.Healthy,
                Resolution = "1920x1080",
                FrameRate = 60,
                Bitrate = 8000
            }
        };

        Assert.Equal("1080p 60fps 8000kbps", enriched.GetQualityDisplay());
    }

    [Fact]
    public void EnrichedStream_GetQualityDisplay_ShowsStatusWhenPending()
    {
        var enriched = new EnrichedStream
        {
            Stream = new Stream { Channel = "Test" },
            Status = StreamResolutionStatus.Pending
        };

        Assert.Equal("Loading...", enriched.GetQualityDisplay());
    }
}