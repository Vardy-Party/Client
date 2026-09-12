using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using VardyParty.LocalService.V2;
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
        _fixture.Inject<IDiscoveredChipNormalizer>(new V2PlaybackTransportPlugin());
    }

    private StreamResolver Sut => _fixture.Create<StreamResolver>();

    private static void SetupResolve(
        Mock<ILocalLanPlayService> localLanPlay,
        string streamUrl,
        string? playerStreamName,
        M3U8Response? response) =>
        localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(
                streamUrl,
                playerStreamName,
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

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
            .With(s => s.Url, "https://stream.example.test/1")
            .With(s => s.Channel, "Channel1")
            .Create();
        var m3u8 = _fixture.Build<M3U8Response>()
            .With(r => r.Url, "https://cdn.example.test/playlist.m3u8?token=abc")
            .Create();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.Healthy)
            .With(h => h.Url, m3u8.Url)
            .With(h => h.Resolution, "1920x1080")
            .With(h => h.FrameRate, 30)
            .With(h => h.Bitrate, 5000)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
        foreach (var stream in streams)
        {
            stream.ResolutionStrategy = "v1";
            stream.Source = "fb";
        }

        var m3u8 = _fixture.Create<M3U8Response>();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.Healthy)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
    public async Task ResolveStreamsIncrementallyAsync_YieldsFastStreamBeforeSlowPeerCompletes()
    {
        var slow = _fixture.Build<Stream>()
            .With(s => s.Url, "https://fb.example.test/slow")
            .With(s => s.Channel, "slow")
            .With(s => s.ResolutionStrategy, "v1")
            .With(s => s.Source, "fb")
            .Create();
        var fast = _fixture.Build<Stream>()
            .With(s => s.Url, "https://fb.example.test/fast")
            .With(s => s.Channel, "fast")
            .With(s => s.ResolutionStrategy, "v1")
            .With(s => s.Source, "fb")
            .Create();

        var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstYield = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var m3u8 = _fixture.Create<M3U8Response>();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.Healthy)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(slow.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await slowGate.Task;
                return m3u8;
            });
        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(fast.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(m3u8);
        _healthChecker
            .Setup(h => h.CheckStreamHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(health);

        var consume = Task.Run(async () =>
        {
            await foreach (var enriched in Sut.ResolveStreamsIncrementallyAsync([slow, fast], batchSize: 2))
            {
                firstYield.TrySetResult(enriched.Stream.Channel);
            }
        });

        var firstChannel = await firstYield.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("fast", firstChannel);

        slowGate.SetResult();
        await consume.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_MpStreams_StaySingleFlight()
    {
        var first = _fixture.Build<Stream>()
            .With(s => s.Url, "https://mp.example.test/a")
            .With(s => s.Channel, "mp-a")
            .With(s => s.PlayerStream, "mp-a")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.StreamStatus, "ready")
            .Create();
        var second = _fixture.Build<Stream>()
            .With(s => s.Url, "https://mp.example.test/b")
            .With(s => s.Channel, "mp-b")
            .With(s => s.PlayerStream, "mp-b")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.StreamStatus, "ready")
            .Create();

        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var maxInFlight = 0;
        var m3u8 = _fixture.Create<M3U8Response>();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.Healthy)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string url, string? _, string? __, string? ___, CancellationToken ____) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                Interlocked.Exchange(ref maxInFlight, Math.Max(maxInFlight, now));
                try
                {
                    if (url == first.Url)
                    {
                        firstEntered.TrySetResult();
                        await firstRelease.Task;
                    }

                    return m3u8;
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            });
        _healthChecker
            .Setup(h => h.CheckStreamHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(health);

        var consume = Task.Run(async () =>
            await CollectAsync(Sut.ResolveStreamsIncrementallyAsync([first, second], batchSize: 2)));

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref inFlight));

        firstRelease.SetResult();
        await consume.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, maxInFlight);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_FailedM3U8Resolution_MarksFailed()
    {
        // Arrange
        var stream = _fixture.Create<Stream>();
        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            .With(s => s.Url, "https://streams.example.test/match")
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
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, stream.PlayerStream, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            s => s.ResolveM3U8UrlAsync(stream.Url, stream.PlayerStream, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_MpChips_EnqueuesRemainingLabels()
    {
        var page = "https://www.example-mp.test/football/match.html";
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, page)
            .With(s => s.Channel, string.Empty)
            .With(s => s.PlayerStream, string.Empty)
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.StreamStatus, "ready")
            .With(s => s.PlayerStreams, new List<string>())
            .Create();

        var first = new M3U8Response
        {
            Url = "https://cdn.example.test/chip-a.m3u8",
            Streams = ["Chip A", "Chip B"],
            SelectedStream = "Chip A",
            RewrittenSegments = ["https://cdn.example.test/seg.exe?_s2=1"]
        };
        var second = new M3U8Response
        {
            Url = "https://cdn.example.test/Chip B.m3u8",
            Streams = ["Chip A", "Chip B"],
            SelectedStream = "Chip B",
            RewrittenSegments = ["https://cdn.example.test/seg2.exe?_s2=1"]
        };

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(page, null, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(first);
        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(page, "Chip B", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(second);
        _healthChecker
            .Setup(h => h.CheckStreamHealthAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<StreamHealthProbe?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, string _, StreamHealthProbe? _, CancellationToken _) =>
                new StreamHealth { Status = StreamHealthStatus.Healthy, Url = url });

        var totals = new List<int>();
        var results = await CollectAsync(Sut.ResolveStreamsIncrementallyAsync(
            [stream],
            batchSize: 1,
            onTotalStreamsKnown: totals.Add));

        Assert.Equal(2, results.Count);
        Assert.Equal("Chip A", results[0].Stream.Channel);
        Assert.Equal("Chip B", results[1].Stream.Channel);
        Assert.Contains(2, totals);
        _localLanPlay.Verify(
            s => s.ResolveM3U8UrlAsync(page, "Chip B", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_MpChips_EnqueuesRemainingWhenFirstHasNoPlaylist()
    {
        var page = "https://www.example-mp.test/football/match.html";
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, page)
            .With(s => s.Channel, string.Empty)
            .With(s => s.PlayerStream, string.Empty)
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.StreamStatus, "ready")
            .With(s => s.PlayerStreams, new List<string>())
            .Create();

        var first = new M3U8Response
        {
            Url = "",
            Streams = ["Chip A", "Chip B"],
            SelectedStream = "Chip A"
        };
        var second = new M3U8Response
        {
            Url = "https://cdn.example.test/Chip B.m3u8",
            Streams = ["Chip A", "Chip B"],
            SelectedStream = "Chip B",
            RewrittenSegments = ["https://cdn.example.test/seg2.exe?_s2=1"]
        };

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(page, null, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(first);
        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(page, "Chip B", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(second);
        _healthChecker
            .Setup(h => h.CheckStreamHealthAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<StreamHealthProbe?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, string _, StreamHealthProbe? _, CancellationToken _) =>
                new StreamHealth { Status = StreamHealthStatus.Healthy, Url = url });

        var results = await CollectAsync(Sut.ResolveStreamsIncrementallyAsync([stream], batchSize: 1));

        Assert.Equal(2, results.Count);
        Assert.Equal("Chip A", results[0].Stream.Channel);
        Assert.Equal(StreamResolutionStatus.Failed, results[0].Status);
        Assert.Equal("Chip B", results[1].Stream.Channel);
        Assert.Equal(StreamResolutionStatus.Healthy, results[1].Status);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_HealthProbeUnreachable_MarksFailed()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.test/match")
            .With(s => s.Channel, "Channel East")
            .With(s => s.PlayerStream, "Channel East")
            .Create();
        var m3u8 = _fixture.Create<M3U8Response>();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.SegmentUnreachable)
            .With(h => h.Url, m3u8.Url)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            .With(s => s.Url, "https://streams.example.test/match")
            .With(s => s.Channel, "Channel East")
            .Create();
        var m3u8 = _fixture.Create<M3U8Response>();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.InvalidManifest)
            .With(h => h.Url, m3u8.Url)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            s => s.ResolveM3U8UrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _healthChecker.Verify(
            h => h.CheckStreamHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveStreamsIncrementallyAsync_RewrittenSegments_UsesProbeHealthOverload()
    {
        // Arrange
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://www.example-mp.test/football/match-1.html")
            .With(s => s.Channel, "Channel North")
            .Create();
        var m3u8 = _fixture.Build<M3U8Response>()
            .With(r => r.Url, "https://streams.example.test/playlist.m3u8")
            .With(r => r.RewrittenSegments, ["https://media.example.test/cfall/seg.exe?_s2=1"])
            .With(r => r.Streams, (List<string>?)null)
            .With(r => r.SelectedStream, (string?)null)
            .Create();
        var health = _fixture.Build<StreamHealth>()
            .With(h => h.Status, StreamHealthStatus.Healthy)
            .Create();

        _localLanPlay
            .Setup(s => s.ResolveM3U8UrlAsync(stream.Url, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(m3u8);
        _healthChecker
            .Setup(h => h.CheckStreamHealthAsync(
                m3u8.Url,
                It.IsAny<string>(),
                It.Is<StreamHealthProbe?>(p => p != null && p.RewrittenSegmentUrls!.Contains("https://media.example.test/cfall/seg.exe?_s2=1")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(health);

        // Act
        var results = await CollectAsync(Sut.ResolveStreamsIncrementallyAsync([stream]));

        // Assert
        Assert.Single(results);
        Assert.Equal(StreamResolutionStatus.Healthy, results[0].Status);
        _healthChecker.Verify(
            h => h.CheckStreamHealthAsync(
                m3u8.Url,
                It.IsAny<string>(),
                It.IsAny<StreamHealthProbe?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static async Task<List<EnrichedStream>> CollectAsync(IAsyncEnumerable<EnrichedStream> source)
    {
        var results = new List<EnrichedStream>();
        await foreach (var item in source)
            results.Add(item);
        return results;
    }
}
