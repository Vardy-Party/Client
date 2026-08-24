using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VardyParty.Parsers;
using Xunit;

namespace VardyParty.Core.Tests;

public class BbcHtmlParserTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    private static string GetResxValue(string name)
    {
        var path = Path.Combine("Resources", name + ".html");
        if (File.Exists(path)) return File.ReadAllText(path);
        throw new FileNotFoundException($"Resource file not found: {path}");
    }

    private BbcHtmlParser CreateParser(IBbcJsonParser? jsonParser = null)
    {
        var logger = NullLogger<BbcHtmlParser>.Instance;
        var parserToUse = jsonParser ?? new BbcJsonParser(NullLogger<BbcJsonParser>.Instance);
        return new BbcHtmlParser(logger, parserToUse);
    }

    [Fact]
    public void ParseHtml_ParsesSampleFragment_HT()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Alpha")
            .AddGame(g =>
                g.WithEventId("s-1").WithHome("Home United").WithAway("Away City").WithScore(1, 0)
                    .WithProgressText("HT"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault(x =>
            x.Home.Contains("Home United", StringComparison.OrdinalIgnoreCase) &&
            x.Away.Contains("Away City", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(f);
        Assert.Equal("HT", f.Status);
        Assert.True(f.IsInProgress);
    }

    [Fact]
    public void ParseHtml_ParsesFinishedGame_FT()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Alpha")
            .AddGame(g =>
                g.WithEventId("s-2").WithHome("Home United").WithAway("Away City").WithScore(2, 1).WithProgressText("FT"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();
        Assert.NotNull(f);
        Assert.Equal("FT", f.Status);
        Assert.True(f.IsFinished);
    }

    [Fact]
    public void ParseHtml_ParsesPostponed_NoProgress()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Gamma")
            .AddGame(g => g.WithEventId("s-3").WithHome("Home United").WithAway("Away City").WithProgressText("Postponed"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();
        Assert.NotNull(f);
        Assert.Equal("Postponed", f.Status);
        Assert.False(f.IsInProgress);
        Assert.False(f.HasProgress);
    }

    [Fact]
    public void ParseHtml_ParsesMatchPostponed_VariedText()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Delta")
            .AddGame(g =>
                g.WithEventId("s-5").WithHome("Home United").WithAway("Away City").WithProgressText("Match postponed due to weather"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();
        Assert.NotNull(f);
        Assert.Equal("Postponed", f.Status);
        Assert.False(f.IsInProgress);
    }

    [Fact]
    public void ParseHtml_InjuryPlusTime_ParsesMinute()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Epsilon")
            .AddGame(g => g.WithEventId("s-6").WithHome("Home United").WithAway("Away City").WithProgressText("90+3"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();
        Assert.NotNull(f);
        Assert.True(f.Minute.HasValue);
        Assert.Equal(9003, f.Minute.Value);
    }

    [Fact]
    public void ParseHtml_CapturedLivePage_LiveDetails_NotStolenFromNextFullTime()
    {
        // Arrange
        // Captured scores-fixtures page while one match was live (85'). The next competition
        // block is already FT; that nearby "Full time" must not finish the live fixture.
        var html = GetResxValue("BbcScoresFixtures_2026-08-01_LiveDetails");
        Assert.Contains("s-8ew7n1ri67qmwp7v5lrcpk9hw", html, StringComparison.Ordinal);
        Assert.Contains("85 minutes , in progress", html, StringComparison.Ordinal);
        Assert.Contains("s-60kcu3gx41jye1nrvbys34bh0", html, StringComparison.Ordinal);
        Assert.Contains("at Full time", html, StringComparison.Ordinal);
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var live = Assert.Single(fixtures, f => f.Minute == 85 && f.HomeScore == 1 && f.AwayScore == 4 && f.IsInProgress);

        Assert.Equal("85'", live.Status);
        Assert.False(live.IsFinished);
        Assert.False(live.IsHalfTime);
        Assert.True(live.HasProgress);
        Assert.False(live.AfterExtraTime);
        Assert.Equal(new DateTime(2026, 8, 1, 18, 0, 0, DateTimeKind.Utc), live.KickoffUtc);
        Assert.False(string.IsNullOrWhiteSpace(live.League));
        Assert.False(string.IsNullOrWhiteSpace(live.HomeBadgeUrl));
        Assert.False(string.IsNullOrWhiteSpace(live.AwayBadgeUrl));
        Assert.EndsWith(".svg", live.HomeBadgeUrl);
        Assert.EndsWith(".svg", live.AwayBadgeUrl);

        Assert.Contains(fixtures, f => f.IsFinished && f.Status == "FT" && f.HomeScore == 1 && f.AwayScore == 1);
    }

    [Fact]
    public void ParseHtml_Badges_AreExtracted()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Zeta")
            .AddGame(g =>
                g.WithEventId("s-7").WithHome("Home United").WithAway("Away City")
                    .WithHomeBadge("https://badges.example.com/h.svg")
                    .WithAwayBadge("https://badges.example.com/a.svg").WithProgressText("Live"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();
        Assert.NotNull(f);
        Assert.Contains("badges.example.com/h.svg", f.HomeBadgeUrl);
        Assert.Contains("badges.example.com/a.svg", f.AwayBadgeUrl);
    }

    [Fact]
    public void ParseHtml_AfterExtraTimeAndPenalties_Parsed()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("Cup Alpha")
            .AddGame(g => g.WithEventId("s-aet-1")
                .WithHome("Home United")
                .WithAway("Away City")
                .WithScore(1, 1)
                .WithHomeBadge("https://badges.example.com/home-united.svg")
                .WithAwayBadge("https://badges.example.com/away-city.svg")
                .WithAfterExtraTime()
                .WithPenaltyResult("Away City", 4, 3))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault(x =>
            x.Home.Contains("Home United", StringComparison.OrdinalIgnoreCase) &&
            x.Away.Contains("Away City", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(f);

        Assert.Equal(1, f.HomeScore);
        Assert.Equal(1, f.AwayScore);
        Assert.True(f.HasProgress);
        Assert.Contains("home-united.svg", f.HomeBadgeUrl);
        Assert.Contains("away-city.svg", f.AwayBadgeUrl);
    }

    [Fact]
    public void ParseHtml_InitialJson_Postponed_Fallback()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Eta")
            .AddGame(g => g.WithEventId("s-x").WithHome("Home United").WithAway("Away City"))
            .BuildPage();

        var mockParser = _fixture.GetMock<IBbcJsonParser>();
        var postponedMap = new Dictionary<string, (string periodLabel, string status, string statusComment)>
        {
            ["s-x"] = ("Postponed", "Postponed", string.Empty)
        };
        mockParser.Setup(p => p.BuildEventMapsStreaming(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((postponedMap, new Dictionary<string, DateTime>()));
        mockParser.Setup(p => p.BuildEventStatusMapStreaming(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(postponedMap);

        var sut = CreateParser(mockParser.Object);

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();
        Assert.NotNull(f);
        Assert.Equal("Postponed", f.Status);
        Assert.False(f.IsInProgress);
    }

    [Fact]
    public void ParseHtml_ParsesManyGames_NoCrash()
    {
        // Arrange
        var builder = new BbcHtmlBuilder().WithLeague("League Beta");
        for (var i = 0; i < 50; i++)
            builder.AddGame(g =>
                g.WithEventId($"s-{i}").WithHome($"Home{i}").WithAway($"Away{i}").WithScore(i % 3, (i + 1) % 3)
                    .WithProgressText(i % 2 == 0 ? "Live" : string.Empty));
        var html = builder.BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        Assert.NotNull(fixtures);
        Assert.Equal(50, fixtures.Count);
    }

    [Fact]
    public void ParseHtml_NoProgress_EmptyStatus()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Theta")
            .AddGame(g => g.WithEventId("s-4").WithHome("Home United").WithAway("Away City"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();
        Assert.NotNull(f);
        Assert.Equal(string.Empty, f.Status);
        Assert.False(f.HasProgress);
    }

    [Fact]
    public void ParseHtml_ParsesInExtraTime_ET_NotFinished()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Alpha")
            .AddGame(g =>
                g.WithEventId("s-et1").WithHome("Home United").WithAway("Away City").WithScore(2, 2)
                    .WithInExtraTime(92))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();
        Assert.NotNull(f);
        Assert.Equal("ET", f.Status);
        Assert.False(f.IsFinished);
        Assert.True(f.IsInProgress);
    }

    [Fact]
    public void BbcHtmlBuilder_Writes_ExtraTime_Markup()
    {
        // Arrange
        var homeBadge = "https://badges.example.com/home-united.svg";
        var awayBadge = "https://badges.example.com/away-city.svg";

        var html = new BbcHtmlBuilder()
            .WithLeague("League Alpha")
            .AddGame(g => g
                .WithEventId("s-et-markup")
                .WithHome("Home United")
                .WithAway("Away City")
                .WithScore(2, 2)
                .WithHomeBadge(homeBadge)
                .WithAwayBadge(awayBadge)
                .WithInExtraTime(100))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault(x =>
            x.Home.Contains("Home United", StringComparison.OrdinalIgnoreCase) &&
            x.Away.Contains("Away City", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(f);

        Assert.Equal("ET", f.Status);
        Assert.False(f.IsFinished);
        Assert.True(f.IsInProgress);

        Assert.True(f.Minute.HasValue);
        Assert.Equal(100, f.Minute.Value);

        Assert.Equal(2, f.HomeScore);
        Assert.Equal(2, f.AwayScore);

        Assert.Contains("home-united.svg", f.HomeBadgeUrl);
        Assert.Contains("away-city.svg", f.AwayBadgeUrl);

        Assert.False(f.AfterExtraTime);
    }

    [Fact]
    public void BbcHtmlBuilder_Writes_Penalties_InProgress_Markup()
    {
        // Arrange
        var builder = new BbcHtmlBuilder()
            .WithLeague("Cup Beta")
            .AddGame(g => g
                .WithHome("Home United")
                .WithAway("Away City")
                .WithScore(0, 0)
                .WithPenalties(3, 2));

        // Act
        var html = builder.BuildPage();

        // Assert
        Assert.Contains("Penalties Home United 3 , Away City 2", html);
        Assert.Contains("Penalties 3-2", html);
    }

    [Fact]
    public void ParseHtml_PenaltiesInProgress_IsNotFinished()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("Cup Beta")
            .AddGame(g => g
                .WithHome("Home United")
                .WithAway("Away City")
                .WithScore(2, 2)
                .WithPenalties(3, 2))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();
        Assert.NotNull(f);

        Assert.Contains("Penalties", f.Status);

        Assert.True(f.IsInProgress, "Game should be considered in progress during penalties");
        Assert.False(f.IsFinished, "Game should not be considered finished during in-progress penalties");

        Assert.Equal(2, f.HomeScore);
        Assert.Equal(2, f.AwayScore);
    }

    [Fact]
    public void ParseHtml_ExtractsTeamLogos_FromCapturedFragment()
    {
        // Arrange
        var html = GetResxValue("ParseHtml_ExtractsTeamLogos");
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = Assert.Single(fixtures);
        Assert.False(string.IsNullOrWhiteSpace(f.Home));
        Assert.False(string.IsNullOrWhiteSpace(f.Away));
        Assert.False(string.IsNullOrWhiteSpace(f.League));
        Assert.False(string.IsNullOrWhiteSpace(f.HomeBadgeUrl));
        Assert.False(string.IsNullOrWhiteSpace(f.AwayBadgeUrl));
    }

    [Fact]
    public void ParseHtml_ExtractsTeamLogos_WithBuilder()
    {
        // Arrange
        var homeLogo = "https://badges.example.com/home-united.svg";
        var awayLogo = "https://badges.example.com/away-city.svg";

        var html = new BbcHtmlBuilder()
            .WithLeague("League Iota")
            .AddGame(g => g
                .WithEventId("s-test-logo")
                .WithHome("Home United")
                .WithAway("Away City")
                .WithHomeBadge(homeLogo)
                .WithAwayBadge(awayLogo))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault(x => x.Home == "Home United" && x.Away == "Away City");

        Assert.NotNull(f);

        Assert.Contains("home-united.svg", f.HomeBadgeUrl);
        Assert.Contains("away-city.svg", f.AwayBadgeUrl);

        Assert.Equal(homeLogo, f.HomeBadgeUrl);
        Assert.Equal(awayLogo, f.AwayBadgeUrl);
    }

    [Fact]
    public void ParseHtml_PlaceholderBadges_ReturnsEmpty()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Alpha")
            .AddGame(g => g
                .WithEventId("s-placeholder")
                .WithHome("Home United")
                .WithAway("Away City")
                .WithHomeBadge("https://badges.example.com/placeholder.svg")
                .WithAwayBadge("https://badges.example.com/real-badge.svg"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();

        Assert.NotNull(f);

        Assert.True(string.IsNullOrEmpty(f.HomeBadgeUrl), "Home badge should be empty when placeholder detected");
        Assert.True(string.IsNullOrEmpty(f.AwayBadgeUrl), "Away badge should be empty when placeholder detected");
    }

    [Fact]
    public void ParseHtml_MissingOneBadge_ReturnsBothEmpty()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Kappa")
            .AddGame(g => g
                .WithEventId("s-missing-badge")
                .WithHome("Home United")
                .WithAway("Away City")
                .WithHomeBadge("https://badges.example.com/home-united.svg")
                .WithAwayBadge(string.Empty))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();

        Assert.NotNull(f);

        Assert.True(string.IsNullOrEmpty(f.HomeBadgeUrl), "Home badge should be empty when away badge is missing");
        Assert.True(string.IsNullOrEmpty(f.AwayBadgeUrl), "Away badge should be empty");
    }

    [Fact]
    public void ParseHtml_FullTimeFixture_ShouldBeMarkedAsFinished()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Alpha")
            .AddGame(g => g
                .WithEventId("s-ft-1")
                .WithHome("Home United")
                .WithAway("Away City")
                .WithScore(1, 3)
                .WithProgressText("Full time"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault(x => x.Home.Contains("Home United", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(f);
        Assert.Equal("FT", f.Status);
        Assert.True(f.IsFinished, "Full-time fixture should be marked as finished");
        Assert.False(f.IsInProgress, "Full-time fixture should not be in progress");
        Assert.Equal(1, f.HomeScore);
        Assert.Equal(3, f.AwayScore);
    }

    [Fact]
    public void ParseHtml_FullTimeFixture_WithAggregateScore_IsFinished()
    {
        // Arrange
        var html = GetResxValue("RawFullTimeAggregateGame");
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();

        Assert.NotNull(f);
        Assert.Equal("FT", f.Status);
        Assert.True(f.IsFinished);
        Assert.False(f.IsInProgress);
        Assert.Equal(1, f.HomeScore);
        Assert.Equal(0, f.AwayScore);
    }

    [Fact]
    public void ParseHtml_CapturedTwoLegPage_ExtractsExpectedAggregateScores()
    {
        // Arrange
        var html = GetResxValue("TwoLegWithAggregateScores");
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        Assert.NotEmpty(fixtures);

        void AssertAggregate(int expectedAggregateHome, int expectedAggregateAway)
        {
            var fixture = fixtures.FirstOrDefault(f =>
                f.AggregateHomeScore == expectedAggregateHome &&
                f.AggregateAwayScore == expectedAggregateAway);

            Assert.NotNull(fixture);
            Assert.True(fixture.HomeScore.HasValue);
            Assert.True(fixture.AwayScore.HasValue);
        }

        AssertAggregate(5, 3);
        AssertAggregate(2, 1);
        AssertAggregate(2, 7);
        AssertAggregate(0, 4);
    }

    [Fact]
    public void ParseHtml_WithAetPenaltiesFixture_IsCorrect()
    {
        // Arrange
        var html = GetResxValue("RawAetPenaltiesGame");
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();

        Assert.NotNull(f);
        Assert.Equal("AET", f.Status);
        Assert.True(f.IsFinished);
        Assert.False(f.IsInProgress);
        Assert.Equal(1, f.HomeScore);
        Assert.Equal(1, f.AwayScore);
        Assert.Equal(4, f.PenaltyWinnerGoals);
        Assert.Equal(2, f.PenaltyLoserGoals);
        Assert.False(string.IsNullOrWhiteSpace(f.PenaltyWinner));
    }

    [Fact]
    public void ParseHtml_FullTimeWithFTAbbreviation_ShouldBeMarkedAsFinished()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Alpha")
            .AddGame(g => g
                .WithEventId("s-ft-test")
                .WithHome("Home United")
                .WithAway("Away City")
                .WithScore(2, 1)
                .WithProgressText("FT"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault();

        Assert.NotNull(f);
        Assert.Equal("FT", f.Status);
        Assert.True(f.IsFinished, "Fixture with FT status should be marked as finished");
    }

    [Fact]
    public void ParseHtml_FullTimeVsInProgress_StatusDifference()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Beta")
            .AddGame(g => g
                .WithEventId("s-ft-game")
                .WithHome("Home United")
                .WithAway("Away City")
                .WithScore(2, 0)
                .WithProgressText("Full time"))
            .AddGame(g => g
                .WithEventId("s-live-game")
                .WithHome("North City")
                .WithAway("South Town")
                .WithScore(1, 1)
                .WithProgressText("67'"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var ftFixture = fixtures.FirstOrDefault(x => x.Home == "Home United");
        var liveFixture = fixtures.FirstOrDefault(x => x.Home == "North City");

        Assert.NotNull(ftFixture);
        Assert.NotNull(liveFixture);

        Assert.Equal("FT", ftFixture.Status);
        Assert.True(ftFixture.IsFinished);
        Assert.False(ftFixture.IsInProgress);

        Assert.Equal("67'", liveFixture.Status);
        Assert.False(liveFixture.IsFinished);
        Assert.True(liveFixture.IsInProgress);
    }

    [Fact]
    public void BbcHtmlBuilder_FullTimeGame_ProducesCorrectMarkup()
    {
        // Arrange
        var homeBadge = "https://badges.example.com/home-united.svg";
        var awayBadge = "https://badges.example.com/away-city.svg";

        var builder = new BbcHtmlBuilder()
            .WithLeague("League Alpha")
            .AddGame(g => g
                .WithEventId("s-ft-markup")
                .WithHome("Home United")
                .WithAway("Away City")
                .WithScore(1, 3)
                .WithHomeBadge(homeBadge)
                .WithAwayBadge(awayBadge)
                .WithProgressText("Full time"));

        // Act
        var html = builder.BuildPage();

        // Assert
        Assert.Contains("data-event-id=\"s-ft-markup\"", html);

        Assert.Contains("Home United", html);
        Assert.Contains("Away City", html);

        Assert.Contains(homeBadge, html);
        Assert.Contains(awayBadge, html);

        Assert.Contains(">1<", html);
        Assert.Contains(">3<", html);

        Assert.Contains("Full time", html);
    }

    [Fact]
    public void BbcHtmlBuilder_FullTimeGame_ParsedCorrectly()
    {
        // Arrange
        var homeBadge = "https://badges.example.com/home-united.svg";
        var awayBadge = "https://badges.example.com/away-city.svg";

        var html = new BbcHtmlBuilder()
            .WithLeague("League Alpha")
            .AddGame(g => g
                .WithEventId("s-ft-parsed")
                .WithHome("Home United")
                .WithAway("Away City")
                .WithScore(1, 3)
                .WithHomeBadge(homeBadge)
                .WithAwayBadge(awayBadge)
                .WithProgressText("Full time"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var fixture = fixtures.FirstOrDefault(x => x.Home == "Home United" && x.Away == "Away City");
        Assert.NotNull(fixture);
        Assert.Equal("Home United", fixture.Home);
        Assert.Equal("Away City", fixture.Away);
        Assert.Equal("League Alpha", fixture.League);
        Assert.Equal(1, fixture.HomeScore);
        Assert.Equal(3, fixture.AwayScore);
        Assert.Equal("FT", fixture.Status);
        Assert.True(fixture.IsFinished, "Full-time fixture should be marked as finished");
        Assert.False(fixture.IsInProgress, "Full-time fixture should not be in progress");
        Assert.Contains("home-united.svg", fixture.HomeBadgeUrl);
        Assert.Contains("away-city.svg", fixture.AwayBadgeUrl);
    }

    [Fact]
    public void ParseHtml_ParsesRawHtmlFragment_FullTimeGame()
    {
        // Arrange
        var html = GetResxValue("RawFullTimeGame");
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var f = fixtures.FirstOrDefault(x => x.HomeScore == 1 && x.AwayScore == 3 && x.Status == "FT");
        Assert.NotNull(f);
        Assert.False(string.IsNullOrWhiteSpace(f.Home));
        Assert.False(string.IsNullOrWhiteSpace(f.Away));
        Assert.Contains(".svg", f.HomeBadgeUrl);
        Assert.Contains(".svg", f.AwayBadgeUrl);
        Assert.True(f.IsFinished);
        Assert.Equal("FT", f.Status);
    }

    [Fact]
    public void ParseHtml_ComplexStatusText_ParsesMinutesAndSpecials()
    {
        // Arrange
        var html = new BbcHtmlBuilder()
            .WithLeague("League Gamma")
            .AddGame(g =>
                g.WithEventId("s-inj").WithHome("Home Injury").WithAway("Away City").WithScore(1, 1).WithProgressText("90+3"))
            .AddGame(g =>
                g.WithEventId("s-et").WithHome("Home Extra").WithAway("Away City").WithScore(2, 2).WithProgressText("100' ET"))
            .AddGame(g =>
                g.WithEventId("s-pen").WithHome("Home Pens").WithAway("Away City").WithScore(0, 0)
                    .WithProgressText("Penalties 5-4"))
            .BuildPage();
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var inj = fixtures.FirstOrDefault(x => x.Home == "Home Injury");
        var et = fixtures.FirstOrDefault(x => x.Home == "Home Extra");
        var pen = fixtures.FirstOrDefault(x => x.Home == "Home Pens");

        Assert.NotNull(inj);
        Assert.NotNull(et);
        Assert.NotNull(pen);

        Assert.True(inj.Minute.HasValue);
        Assert.Equal(9003, inj.Minute.Value);
        Assert.True(inj.IsInProgress);

        Assert.Equal("ET", et.Status);
        Assert.True(et.Minute.HasValue);
        Assert.Equal(100, et.Minute.Value);
        Assert.True(et.IsInProgress);

        Assert.Contains("Penalties", pen.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(pen.IsInProgress);
    }

    [Fact]
    public void ParseHtml_CapturedPreMatchPage_NotInProgress()
    {
        // Arrange
        var html = GetResxValue("RawInProgressIssue");
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        Assert.NotEmpty(fixtures);
        Assert.DoesNotContain(fixtures, f => f.IsInProgress && string.IsNullOrEmpty(f.Status));
        Assert.Contains(fixtures, f => !f.IsInProgress && !f.HasProgress && f.Status == string.Empty);
    }

    [Fact]
    public void ParseHtml_CapturedTwoLegPage_ExtractsAggregateScores_WhenGameNotStarted()
    {
        // Arrange
        var html = GetResxValue("TwoLegBeforeStart");
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        Assert.NotEmpty(fixtures);

        void AssertPreMatchAggregate(int expectedAggregateHome, int expectedAggregateAway)
        {
            var fixture = fixtures.FirstOrDefault(f =>
                f.AggregateHomeScore == expectedAggregateHome &&
                f.AggregateAwayScore == expectedAggregateAway);

            Assert.NotNull(fixture);
            Assert.False(fixture.IsInProgress);
            Assert.False(fixture.IsFinished);
        }

        AssertPreMatchAggregate(1, 1);
        AssertPreMatchAggregate(6, 1);
        AssertPreMatchAggregate(0, 1);
        AssertPreMatchAggregate(2, 5);
    }

    [Fact]
    public void ParseHtml_CapturedTwoLegPage_ExtractsKickoffTimes()
    {
        // Arrange
        var html = GetResxValue("TwoLegBeforeStart");
        var sut = CreateParser();

        // Act
        var fixtures = sut.ParseHtml(html);

        // Assert
        var fixture = fixtures.FirstOrDefault(f =>
            f.AggregateHomeScore == 1 && f.AggregateAwayScore == 1);

        Assert.NotNull(fixture);
        Assert.Equal(new DateTime(2026, 3, 18, 17, 45, 0, DateTimeKind.Utc), fixture.KickoffUtc);
    }
}
