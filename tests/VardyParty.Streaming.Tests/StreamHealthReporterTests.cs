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
}
