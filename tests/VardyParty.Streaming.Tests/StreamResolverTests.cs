using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Streaming;
using VardyParty.TestSupport;

namespace VardyParty.Streaming.Tests;

public class StreamResolverTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();
    private readonly Mock<IStreamHealthChecker> _healthChecker;
    private readonly Mock<ILocalLanPlayService> _localLanPlay;

    public StreamResolverTests()
    {
        _healthChecker = _fixture.GetMock<IStreamHealthChecker>();
        _localLanPlay = _fixture.GetMock<ILocalLanPlayService>();
    }

    private StreamResolver Sut => _fixture.Create<StreamResolver>();

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_EmptyList_YieldsNothing()
    {
        // Arrange
        var sut = Sut;

        // Act
        var results = await CollectAsync(sut.ResolveStreamsIncrementallyAsync([]));

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_SingleStream_ResolvesAndTests()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "http://stream.com/1")
            .With(s => s.Channel, "Channel1")
            .Create();
        var m3u8 = _fixture.Build<M3U8Response>()
            .With(r => r.Url, "http://m3u8.com/playlist.m3u8?token=abc")
            .Create();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.Healthy)
            .With(h => h.Url, m3u8.Url)
            .With(h => h.Resolution, "1920x1080")
            .With(h => h.FrameRate, 30)
            .With(h => h.Bitrate, 5000)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(m3u8);
        _healthChecker
            .Setup(h => h.CheckStreamHealthAsync(m3u8.Url, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(health);

        // Act
        var results = await CollectAsync(Sut.ResolveStreamsIncrementallyAsync([stream]));

        // Assert
        Assert.Single(results);
        Assert.Equal(stream.Channel, results[0].Stream.Channel);
        Assert.Equal(m3u8.Url, results[0].ResolvedM3U8Url);
        Assert.Equal(StreamResolutionStatus.Healthy, results[0].Status);
        Assert.NotNull(results[0].Health);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_MultipleStreams_ProcessesInBatches()
    {
        // Arrange
        var streams = _fixture.CreateMany<Stream>(5).ToList();
        var m3u8 = _fixture.Create<M3U8Response>();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.Healthy)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(m3u8);
        _healthChecker
            .Setup(h => h.CheckStreamHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(health);

        // Act
        var results = await CollectAsync(Sut.ResolveStreamsIncrementallyAsync(streams, 2));

        // Assert
        Assert.Equal(5, results.Count);
        Assert.All(results, r => Assert.Equal(StreamResolutionStatus.Healthy, r.Status));
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_FailedM3U8Resolution_MarksFailed()
    {
        // Arrange
        var stream = _fixture.Create<Stream>();
        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((M3U8Response?)null);

        // Act
        var results = await CollectAsync(Sut.ResolveStreamsIncrementallyAsync([stream]));

        // Assert
        Assert.Single(results);
        Assert.Equal(StreamResolutionStatus.Failed, results[0].Status);
        Assert.NotNull(results[0].ErrorMessage);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_SegmentUnreachable_MarksFailed()
    {
        // Arrange
        var stream = _fixture.Create<Stream>();
        var m3u8 = _fixture.Create<M3U8Response>();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.SegmentUnreachable)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(m3u8);
        _healthChecker
            .Setup(h => h.CheckStreamHealthAsync(m3u8.Url, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(health);

        // Act
        var results = await CollectAsync(Sut.ResolveStreamsIncrementallyAsync([stream]));

        // Assert
        Assert.Single(results);
        Assert.Equal(StreamResolutionStatus.Failed, results[0].Status);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_InvalidManifestHealthCheck_MarksFailed()
    {
        // Arrange
        var stream = _fixture.Create<Stream>();
        var m3u8 = _fixture.Create<M3U8Response>();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.InvalidManifest)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(m3u8);
        _healthChecker
            .Setup(h => h.CheckStreamHealthAsync(m3u8.Url, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(health);

        // Act
        var results = await CollectAsync(Sut.ResolveStreamsIncrementallyAsync([stream]));

        // Assert
        Assert.Single(results);
        Assert.Equal(StreamResolutionStatus.Failed, results[0].Status);
    }

    [Fact]
    public async Task ResolveM3U8UrlAsync_ResolutionFails_ReturnsNull()
    {
        // Arrange
        var stream = _fixture.Create<Stream>();
        var referer = _fixture.Create<Uri>().ToString();
        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((M3U8Response?)null);

        // Act
        var result = await Sut.ResolveM3U8UrlAsync(stream, referer);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveM3U8UrlAsync_ResolutionSucceeds_ReturnsUrl()
    {
        // Arrange
        var stream = _fixture.Create<Stream>();
        var m3u8 = _fixture.Create<M3U8Response>();
        var referer = _fixture.Create<Uri>().ToString();
        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(m3u8);

        // Act
        var result = await Sut.ResolveM3U8UrlAsync(stream, referer);

        // Assert
        Assert.Equal(m3u8.Url, result);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_V2PlayerStream_PassesLabelToLocalService()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/match")
            .With(s => s.Channel, "Channel North")
            .With(s => s.PlayerStream, "Channel North")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.StreamStatus, "ready")
            .Create();
        var m3u8 = _fixture.Create<M3U8Response>();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.Healthy)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, stream.PlayerStream, It.IsAny<CancellationToken>()))
            .ReturnsAsync(m3u8);
        _healthChecker
            .Setup(h => h.CheckStreamHealthAsync(m3u8.Url, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(health);

        // Act
        var results = await CollectAsync(Sut.ResolveStreamsIncrementallyAsync([stream]));

        // Assert
        Assert.Single(results);
        Assert.Equal(StreamResolutionStatus.Healthy, results[0].Status);
        _localLanPlay.Verify(
            s => s.ResolveM3U8UrlAsync(stream.Url, stream.PlayerStream, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_HealthProbeUnreachable_MarksFailed()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/match")
            .With(s => s.Channel, "Channel East")
            .With(s => s.PlayerStream, "Channel East")
            .Create();
        var m3u8 = _fixture.Create<M3U8Response>();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.SegmentUnreachable)
            .With(h => h.Url, m3u8.Url)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(m3u8);
        _healthChecker
            .Setup(h => h.CheckStreamHealthAsync(m3u8.Url, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(health);

        // Act
        var results = await CollectAsync(Sut.ResolveStreamsIncrementallyAsync([stream]));

        // Assert
        Assert.Single(results);
        Assert.Equal(StreamResolutionStatus.Failed, results[0].Status);
        Assert.Equal(m3u8.Url, results[0].ResolvedM3U8Url);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_HealthProbeInvalidManifest_StillFails()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/match")
            .With(s => s.Channel, "Channel East")
            .Create();
        var m3u8 = _fixture.Create<M3U8Response>();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.InvalidManifest)
            .With(h => h.Url, m3u8.Url)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(m3u8);
        _healthChecker
            .Setup(h => h.CheckStreamHealthAsync(m3u8.Url, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(health);

        // Act
        var results = await CollectAsync(Sut.ResolveStreamsIncrementallyAsync([stream]));

        // Assert
        Assert.Single(results);
        Assert.Equal(StreamResolutionStatus.Failed, results[0].Status);
    }

    [Fact]
    public void EnrichedStream_IsReadyForPlayback_ReturnsTrueWhenHealthy()
    {
        // Arrange
        var stream = _fixture.Create<Stream>();
        var m3u8 = _fixture.Create<M3U8Response>();
        var enriched = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, stream)
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.ResolvedM3U8Url, m3u8.Url)
            .With(e => e.Health, _fixture.Build<StreamHealth>()
                .With(h => h.Status, StreamHealthStatus.Healthy)
                .Create())
            .Create();

        // Act
        var ready = enriched.IsReadyForPlayback;

        // Assert
        Assert.True(ready);
    }

    [Fact]
    public void EnrichedStream_GetQualityDisplay_ShowsQualityWhenHealthy()
    {
        // Arrange
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.Healthy)
            .With(h => h.Resolution, "1920x1080")
            .With(h => h.FrameRate, 60)
            .With(h => h.Bitrate, 8000)
            .Create();
        var enriched = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, _fixture.Create<Stream>())
            .With(e => e.Status, StreamResolutionStatus.Healthy)
            .With(e => e.Health, health)
            .Create();

        // Act
        var display = enriched.GetQualityDisplay();

        // Assert
        Assert.Equal("1080p 60fps 8000kbps", display);
    }

    [Fact]
    public void EnrichedStream_GetQualityDisplay_ShowsStatusWhenPending()
    {
        // Arrange
        var enriched = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, _fixture.Create<Stream>())
            .With(e => e.Status, StreamResolutionStatus.Pending)
            .Without(e => e.Health)
            .Create();

        // Act
        var display = enriched.GetQualityDisplay();

        // Assert
        Assert.Equal("Loading...", display);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_CountdownStream_IsSkippedWithoutResolve()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.StreamStatus, "countdown")
            .Create();

        // Act
        var results = await CollectAsync(Sut.ResolveStreamsIncrementallyAsync([stream]));

        // Assert
        Assert.Single(results);
        Assert.Equal(StreamResolutionStatus.Failed, results[0].Status);
        Assert.Contains("countdown", results[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
        _localLanPlay.Verify(
            s => s.ResolveM3U8UrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _healthChecker.Verify(
            h => h.CheckStreamHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static async Task<List<EnrichedStream>> CollectAsync(IAsyncEnumerable<EnrichedStream> source)
    {
        var results = new List<EnrichedStream>();
        await foreach (var item in source)
            results.Add(item);
        return results;
    }
}
