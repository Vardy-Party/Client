using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VardyParty.Configuration;
using VardyParty.Models;
using Xunit;

namespace VardyParty.Tests;

public class BbcFixturesServiceTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task GetRollingWindowFixturesAsync_FetchesTodayAndTomorrowUkPages()
    {
        // Arrange
        var requestedUrls = new List<string>();
        var handler = new StubHttpHandler(requestedUrls);
        var httpClient = new HttpClient(handler);
        var parser = _fixture.GetMock<IBbcHtmlParser>();
        parser.Setup(x => x.ParseHtml(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, _) => []);

        var settings = _fixture.Build<BbcFixturesSettings>()
            .With(s => s.CallTimeoutSeconds, 5)
            .With(s => s.MaxRetries, 0)
            .With(s => s.RefreshSchedule, 300)
            .Create();

        var service = new BbcFixturesService(
            httpClient,
            Options.Create(settings),
            NullLogger<BbcFixturesService>.Instance,
            parser.Object);

        var pageDates = BbcFixtureSchedule.GetRollingWindowPageDates(DateTime.UtcNow);

        // Act
        await service.GetFixturesForDatesAsync(pageDates);

        // Assert
        Assert.Equal(pageDates.Count, requestedUrls.Count);
        Assert.All(requestedUrls, url => Assert.Contains("/sport/football/scores-fixtures/", url));
        Assert.NotEqual(requestedUrls[0], requestedUrls[1]);
    }

    [Fact]
    public async Task GetFixturesForDatesAsync_MergesDuplicateFixturesPreferringKickoff()
    {
        // Arrange
        var handler = new StubHttpHandler(["https://example.com/today", "https://example.com/tomorrow"]);
        var httpClient = new HttpClient(handler);
        var parser = _fixture.GetMock<IBbcHtmlParser>();
        parser.SetupSequence(x => x.ParseHtml(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns([
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = DateTime.MinValue,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = string.Empty,
                    AwayBadgeUrl = string.Empty,
                    League = "League Alpha",
                    HasProgress = false
                }
            ])
            .Returns([
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = new DateTime(2026, 6, 12, 23, 0, 0, DateTimeKind.Utc),
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "home.svg",
                    AwayBadgeUrl = "away.svg",
                    League = "League Alpha",
                    HasProgress = false
                }
            ]);

        var settings = _fixture.Build<BbcFixturesSettings>()
            .With(s => s.CallTimeoutSeconds, 5)
            .With(s => s.MaxRetries, 0)
            .With(s => s.RefreshSchedule, 300)
            .Create();

        var service = new BbcFixturesService(
            httpClient,
            Options.Create(settings),
            NullLogger<BbcFixturesService>.Instance,
            parser.Object);

        // Act
        var fixtures = await service.GetFixturesForDatesAsync(
            [new DateOnly(2026, 6, 12), new DateOnly(2026, 6, 13)]);

        // Assert
        var fixture = Assert.Single(fixtures);
        Assert.Equal(new DateTime(2026, 6, 12, 23, 0, 0, DateTimeKind.Utc), fixture.KickoffUtc);
        Assert.Equal("home.svg", fixture.HomeBadgeUrl);
    }

    [Fact]
    public async Task GetFixturesForDatesAsync_MergesNormalizedNameVariantsPreferringKickoff()
    {
        // Arrange
        var handler = new StubHttpHandler(["https://example.com/today", "https://example.com/tomorrow"]);
        var httpClient = new HttpClient(handler);
        var parser = _fixture.GetMock<IBbcHtmlParser>();
        parser.SetupSequence(x => x.ParseHtml(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns([
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home Utd",
                    Away = "Away City",
                    KickoffUtc = DateTime.MinValue,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = string.Empty,
                    AwayBadgeUrl = string.Empty,
                    League = "League Alpha",
                    HasProgress = false
                }
            ])
            .Returns([
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = new DateTime(2026, 6, 13, 1, 0, 0, DateTimeKind.Utc),
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "home.svg",
                    AwayBadgeUrl = "away.svg",
                    League = "League Alpha",
                    HasProgress = false
                }
            ]);

        var settings = _fixture.Build<BbcFixturesSettings>()
            .With(s => s.CallTimeoutSeconds, 5)
            .With(s => s.MaxRetries, 0)
            .With(s => s.RefreshSchedule, 300)
            .Create();

        var service = new BbcFixturesService(
            httpClient,
            Options.Create(settings),
            NullLogger<BbcFixturesService>.Instance,
            parser.Object);

        // Act
        var fixtures = await service.GetFixturesForDatesAsync(
            [new DateOnly(2026, 6, 12), new DateOnly(2026, 6, 13)]);

        // Assert
        var fixture = Assert.Single(fixtures);
        Assert.Equal(new DateTime(2026, 6, 13, 1, 0, 0, DateTimeKind.Utc), fixture.KickoffUtc);
        Assert.Equal("home.svg", fixture.HomeBadgeUrl);
    }

    private sealed class StubHttpHandler(List<string> requestedUrls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requestedUrls.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html></html>")
            });
        }
    }
}
