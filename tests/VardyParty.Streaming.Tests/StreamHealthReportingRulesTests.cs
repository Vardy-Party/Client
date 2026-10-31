using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Kernel;
using VardyParty.Streaming;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// Business rules for crowd health reports: one stream has one key, and the
/// same sample is not sent twice (16:49:27 and 16:49:29 UK time, 31 Oct 2026).
/// </summary>
public class StreamHealthReportingRulesTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();
    private readonly List<StreamHealthReport> _sent = [];
    private DateTimeOffset _now = new(2026, 10, 31, 16, 49, 27, TimeSpan.Zero);

    private StreamHealthReporter CreateReporter()
    {
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.ApiLeague, "league-alpha")
            .Create();
        _fixture.Freeze<SelectionState>().CurrentGame = game;
        _fixture.GetMock<IStreamHealthService>()
            .Setup(s => s.ReportHealthAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<StreamHealthReport>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, StreamHealthReport, CancellationToken>((_, _, _, r, _) => _sent.Add(r))
            .Returns(Task.CompletedTask);
        var sut = _fixture.Create<StreamHealthReporter>();
        sut.Clock = () => _now;
        return sut;
    }

    [Fact]
    public async Task WorkingSample_FromTwoTimers_IsSentOnce()
    {
        var sut = CreateReporter();

        await sut.ReportPlaybackMetricsAsync("https://streams.example.com/match", null);
        _now = _now.AddSeconds(2);
        await sut.ReportPlaybackMetricsAsync("https://streams.example.com/match", null);

        Assert.Single(_sent);
    }

    [Fact]
    public async Task WorkingSample_AfterWindow_IsSentAgain()
    {
        var sut = CreateReporter();

        await sut.ReportPlaybackMetricsAsync("https://streams.example.com/match", null);
        _now = _now.Add(StreamHealthReporter.DuplicateWindow);
        await sut.ReportPlaybackMetricsAsync("https://streams.example.com/match", null);

        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task StartedReport_IsDedupedSeparatelyFromTicks()
    {
        var sut = CreateReporter();

        await sut.ReportPlaybackMetricsAsync("https://streams.example.com/match", null);
        await sut.ReportPlaybackStartedAsync("https://streams.example.com/match", null);
        await sut.ReportPlaybackStartedAsync("https://streams.example.com/match", null);

        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task Failure_IsReportedOncePerMinute_PerStream()
    {
        var sut = CreateReporter();

        await sut.ReportPlaybackErrorAsync("https://streams.example.com/a", null, error: "dead");
        _now = _now.AddSeconds(30);
        await sut.ReportPlaybackErrorAsync("https://streams.example.com/a", null, error: "dead");
        await sut.ReportPlaybackErrorAsync("https://streams.example.com/b", null, error: "dead");
        _now = _now.AddSeconds(31);
        await sut.ReportPlaybackErrorAsync("https://streams.example.com/a", null, error: "dead");

        Assert.Equal(3, _sent.Count);
    }

    [Fact]
    public async Task UserReport_IsNeverSuppressed()
    {
        var sut = CreateReporter();

        await sut.ReportBadStreamAsync("https://streams.example.com/a", null);
        await sut.ReportBadStreamAsync("https://streams.example.com/a", null);

        Assert.Equal(2, _sent.Count);
        Assert.All(_sent, r => Assert.Equal("user-report", r.Status));
    }

    [Fact]
    public void PlaybackReportIdentity_PrefersCatalogUrlOverEmbedReferer()
    {
        var stream = _fixture.Build<Stream>()
            .With(s => s.Url, "https://streams.example.com/match.html")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.PlayerStream, "Channel East")
            .With(s => s.StreamStatus, "ready")
            .Create();
        var current = new EnrichedStream { Stream = stream };

        var (url, name) = StreamHealthIdentity.ResolvePlaybackReportIdentity(
            current, "https://cdn.example.com/live/index.m3u8?token=x", "https://dembed.st/");

        Assert.Equal("https://streams.example.com/match.html", url);
        Assert.Equal(StreamHealthIdentity.GetStreamName(stream), name);
    }

    [Fact]
    public void PlaybackReportIdentity_WithoutCurrentStream_FallsBackToReferer()
    {
        var (url, name) = StreamHealthIdentity.ResolvePlaybackReportIdentity(
            null, "https://cdn.example.com/live/index.m3u8?token=x", "https://streams.example.com/page");

        Assert.Equal("https://streams.example.com/page", url);
        Assert.Null(name);
    }
}