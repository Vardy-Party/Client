using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using VardyParty.Configuration;
using VardyParty.Models;
using VardyParty.Parsers;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests;

public class BbcFixturesServiceTests
{
    [Fact]
    public async Task GetRollingWindowFixturesAsync_FetchesTodayAndTomorrowUkPages()
    {
        var requestedUrls = new List<string>();
        var handler = new StubHttpHandler(requestedUrls);
        var httpClient = new HttpClient(handler);
        var parser = new Mock<IBbcHtmlParser>();
        parser.Setup(x => x.ParseHtml(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, _) => []);

        var settings = new BbcFixturesSettings
        {
            CallTimeoutSeconds = 5,
            MaxRetries = 0,
            RefreshSchedule = 300
        };

        var service = new BbcFixturesService(
            httpClient,
            Options.Create(settings),
            NullLogger<BbcFixturesService>.Instance,
            parser.Object);

        var pageDates = BbcFixtureSchedule.GetRollingWindowPageDates(DateTime.UtcNow);
        await service.GetFixturesForDatesAsync(pageDates);

        Assert.Equal(pageDates.Count, requestedUrls.Count);
        Assert.All(requestedUrls, url => Assert.Contains("/sport/football/scores-fixtures/", url));
        Assert.NotEqual(requestedUrls[0], requestedUrls[1]);
    }

    [Fact]
    public async Task GetFixturesForDatesAsync_MergesDuplicateFixturesPreferringKickoff()
    {
        var handler = new StubHttpHandler(["https://example.com/today", "https://example.com/tomorrow"]);
        var httpClient = new HttpClient(handler);
        var parser = new Mock<IBbcHtmlParser>();
        parser.SetupSequence(x => x.ParseHtml(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns([
                new BbcFixture("United States", "Paraguay", DateTime.MinValue, "", false, false, false, null,
                    null, null, string.Empty, string.Empty, "Important Games", false)
            ])
            .Returns([
                new BbcFixture("United States", "Paraguay",
                    new DateTime(2026, 6, 12, 23, 0, 0, DateTimeKind.Utc), "", false, false, false, null,
                    null, null, "usa.svg", "paraguay.svg", "Important Games", false)
            ]);

        var settings = new BbcFixturesSettings
        {
            CallTimeoutSeconds = 5,
            MaxRetries = 0,
            RefreshSchedule = 300
        };

        var service = new BbcFixturesService(
            httpClient,
            Options.Create(settings),
            NullLogger<BbcFixturesService>.Instance,
            parser.Object);

        var fixtures = await service.GetFixturesForDatesAsync(
            [new DateOnly(2026, 6, 12), new DateOnly(2026, 6, 13)]);

        var fixture = Assert.Single(fixtures);
        Assert.Equal(new DateTime(2026, 6, 12, 23, 0, 0, DateTimeKind.Utc), fixture.KickoffUtc);
        Assert.Equal("usa.svg", fixture.HomeBadgeUrl);
    }

    [Fact]
    public async Task GetFixturesForDatesAsync_MergesUsaAliasVariantsPreferringKickoff()
    {
        var handler = new StubHttpHandler(["https://example.com/today", "https://example.com/tomorrow"]);
        var httpClient = new HttpClient(handler);
        var parser = new Mock<IBbcHtmlParser>();
        parser.SetupSequence(x => x.ParseHtml(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns([
                new BbcFixture("USA", "Paraguay", DateTime.MinValue, "", false, false, false, null,
                    null, null, string.Empty, string.Empty, "Important Games", false)
            ])
            .Returns([
                new BbcFixture("United States", "Paraguay",
                    new DateTime(2026, 6, 13, 1, 0, 0, DateTimeKind.Utc), "", false, false, false, null,
                    null, null, "usa.svg", "paraguay.svg", "Important Games", false)
            ]);

        var settings = new BbcFixturesSettings
        {
            CallTimeoutSeconds = 5,
            MaxRetries = 0,
            RefreshSchedule = 300
        };

        var service = new BbcFixturesService(
            httpClient,
            Options.Create(settings),
            NullLogger<BbcFixturesService>.Instance,
            parser.Object);

        var fixtures = await service.GetFixturesForDatesAsync(
            [new DateOnly(2026, 6, 12), new DateOnly(2026, 6, 13)]);

        var fixture = Assert.Single(fixtures);
        Assert.Equal(new DateTime(2026, 6, 13, 1, 0, 0, DateTimeKind.Utc), fixture.KickoffUtc);
        Assert.Equal("usa.svg", fixture.HomeBadgeUrl);
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
