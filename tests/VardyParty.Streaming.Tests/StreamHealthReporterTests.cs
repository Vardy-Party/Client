using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Streaming;
using VardyParty.TestSupport;

namespace VardyParty.Streaming.Tests;

public class StreamHealthReporterTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task ReportPlaybackStarted_PrefersPageUrlOverM3U8()
    {
        // Arrange
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.ApiLeague, "league-alpha")
            .Create();
        var selection = _fixture.Freeze<SelectionState>();
        selection.CurrentGame = game;
        _fixture.GetMock<ISessionIdProvider>().Setup(p => p.SessionId).Returns("session-north");
        StreamHealthReport? captured = null;
        _fixture.GetMock<IStreamHealthService>()
            .Setup(s => s.ReportHealthAsync(
                game.ApiLeague,
                game.Home,
                game.Away,
                It.IsAny<StreamHealthReport>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, StreamHealthReport, CancellationToken>((_, _, _, report, _) =>
                captured = report)
            .Returns(Task.CompletedTask);
        var sut = _fixture.Create<StreamHealthReporter>();

        // Act
        await sut.ReportPlaybackStartedAsync(
            "https://cdn.example.com/live/playlist.m3u8?token=abc",
            "https://streams.example.com/match.html");

        // Assert
        Assert.NotNull(captured);
        Assert.Equal("https://streams.example.com/match.html", captured.StreamUrl);
        Assert.Equal("working", captured.Status);
    }

    [Fact]
    public async Task ReportPlaybackError_UsesStreamUrlWhenRefererMissing()
    {
        // Arrange
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.ApiLeague, "league-alpha")
            .Create();
        var selection = _fixture.Freeze<SelectionState>();
        selection.CurrentGame = game;
        StreamHealthReport? captured = null;
        _fixture.GetMock<IStreamHealthService>()
            .Setup(s => s.ReportHealthAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<StreamHealthReport>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, StreamHealthReport, CancellationToken>((_, _, _, report, _) =>
                captured = report)
            .Returns(Task.CompletedTask);
        var sut = _fixture.Create<StreamHealthReporter>();
        const string pageUrl = "https://streams.example.com/channel-north";

        // Act
        await sut.ReportPlaybackErrorAsync(pageUrl, null, error: "decoder");

        // Assert
        Assert.NotNull(captured);
        Assert.Equal(pageUrl, captured.StreamUrl);
        Assert.Equal("failed", captured.Status);
    }

    [Fact]
    public async Task ReportPlaybackMetrics_SendsMeasuredResolutionAndBitrate()
    {
        // Arrange — periodic ticks must carry live picture size + bitrate to /health
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.ApiLeague, "league-alpha")
            .Create();
        var selection = _fixture.Freeze<SelectionState>();
        selection.CurrentGame = game;
        StreamHealthReport? captured = null;
        _fixture.GetMock<IStreamHealthService>()
            .Setup(s => s.ReportHealthAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<StreamHealthReport>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, StreamHealthReport, CancellationToken>((_, _, _, report, _) =>
                captured = report)
            .Returns(Task.CompletedTask);
        var sut = _fixture.Create<StreamHealthReporter>();
        var metrics = new PlaybackMetrics
        {
            Resolution = (960, 540),
            BitrateKbps = 1840,
            Framerate = 30
        };

        // Act
        await sut.ReportPlaybackMetricsAsync(
            "https://streams.example.com/channel-north",
            null,
            streamName: "Channel North",
            metrics: metrics);

        // Assert
        Assert.NotNull(captured);
        Assert.Equal("working", captured.Status);
        Assert.Equal("960x540", captured.Resolution);
        Assert.Equal(1840, captured.Bitrate);
        Assert.Equal("good", captured.Quality);
        Assert.Null(captured.Framerate); // codecs/fps stay on the started report only
    }
}
