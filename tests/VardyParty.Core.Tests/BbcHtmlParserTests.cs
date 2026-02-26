using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VardyParty.Parsers;
using Xunit;

namespace VardyParty.Core.Tests;

public class BbcHtmlParserTests
{
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
        var html = new BbcHtmlBuilder()
            .WithLeague("Premier League")
            .AddGame(g =>
                g.WithEventId("s-1").WithHome("Manchester City").WithAway("Chelsea").WithScore(1, 0)
                    .WithProgressText("HT"))
            .BuildPage();

        var fixtures = CreateParser().ParseHtml(html);
        var f = fixtures.FirstOrDefault(x =>
            x.Home.Contains("Manchester City", StringComparison.OrdinalIgnoreCase) &&
            x.Away.Contains("Chelsea", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(f);
        Assert.Equal("HT", f.Status);
        Assert.True(f.IsInProgress);
    }

    [Fact]
    public void ParseHtml_ParsesFinishedGame_FT()
    {
        var html = new BbcHtmlBuilder()
            .WithLeague("Premier League")
            .AddGame(g =>
                g.WithEventId("s-2").WithHome("TeamA").WithAway("TeamB").WithScore(2, 1).WithProgressText("FT"))
            .BuildPage();

        var f = CreateParser().ParseHtml(html).FirstOrDefault();
        Assert.NotNull(f);
        Assert.Equal("FT", f.Status);
        Assert.True(f.IsFinished);
    }

    [Fact]
    public void ParseHtml_ParsesPostponed_NoProgress()
    {
        var html = new BbcHtmlBuilder()
            .WithLeague("LeagueX")
            .AddGame(g => g.WithEventId("s-3").WithHome("Damac").WithAway("Other").WithProgressText("Postponed"))
            .BuildPage();

        var f = CreateParser().ParseHtml(html).FirstOrDefault();
        Assert.NotNull(f);
        Assert.Equal("Postponed", f.Status);
        Assert.False(f.IsInProgress);
        Assert.False(f.HasProgress);
    }

    [Fact]
    public void ParseHtml_ParsesMatchPostponed_VariedText()
    {
        var html = new BbcHtmlBuilder()
            .WithLeague("LeagueZ")
            .AddGame(g =>
                g.WithEventId("s-5").WithHome("A").WithAway("B").WithProgressText("Match postponed due to weather"))
            .BuildPage();

        var f = CreateParser().ParseHtml(html).FirstOrDefault();
        Assert.NotNull(f);
        Assert.Equal("Postponed", f.Status);
        Assert.False(f.IsInProgress);
    }

    [Fact]
    public void ParseHtml_InjuryPlusTime_ParsesMinute()
    {
        var html = new BbcHtmlBuilder()
            .WithLeague("LeagueI")
            .AddGame(g => g.WithEventId("s-6").WithHome("X").WithAway("Y").WithProgressText("90+3"))
            .BuildPage();

        var f = CreateParser().ParseHtml(html).FirstOrDefault();
        Assert.NotNull(f);
        Assert.True(f.Minute.HasValue);
        Assert.Equal(9003, f.Minute.Value);
    }

    [Fact]
    public void ParseHtml_Badges_AreExtracted()
    {
        var html = new BbcHtmlBuilder()
            .WithLeague("LeagueB")
            .AddGame(g =>
                g.WithEventId("s-7").WithHome("HB").WithAway("AB").WithHomeBadge("https://example.com/h.svg")
                    .WithAwayBadge("https://example.com/a.svg").WithProgressText("Live"))
            .BuildPage();

        var f = CreateParser().ParseHtml(html).FirstOrDefault();
        Assert.NotNull(f);
        // Parser should extract the exact SVG URLs provided in the HTML
        Assert.Contains("example.com/h.svg", f.HomeBadgeUrl);
        Assert.Contains("example.com/a.svg", f.AwayBadgeUrl);
    }

    [Fact]
    public void ParseHtml_AfterExtraTimeAndPenalties_Parsed()
    {
        var html = new BbcHtmlBuilder()
            .WithLeague("FA Cup")
            .AddGame(g => g.WithEventId("s-b1ky54ud4knpr8o4m79jfup78")
                .WithHome("Milton Keynes Dons")
                .WithAway("Oxford United")
                .WithScore(1, 1)
                .WithHomeBadge(
                    "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/milton-keynes-dons.0c37c8c1e0.svg")
                .WithAwayBadge(
                    "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/oxford-united.43e728f198.svg")
                .WithAfterExtraTime()
                .WithPenaltyResult("Oxford United", 4, 3))
            .BuildPage();

        var fixtures = CreateParser().ParseHtml(html);
        var f = fixtures.FirstOrDefault(x =>
            x.Home.Contains("Milton Keynes", StringComparison.OrdinalIgnoreCase) &&
            x.Away.Contains("Oxford United", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(f);

        // Scores at end of extra time should be 1-1
        Assert.Equal(1, f.HomeScore);
        Assert.Equal(1, f.AwayScore);

        // Parser should mark this fixture as having progress (AET/penalties)
        Assert.True(f.HasProgress);

        // Badges should be extracted
        Assert.Contains("milton-keynes-dons.0c37c8c1e0.svg", f.HomeBadgeUrl);
        Assert.Contains("oxford-united.43e728f198.svg", f.AwayBadgeUrl);
    }

    [Fact]
    public void ParseHtml_InitialJson_Postponed_Fallback()
    {
        // build initial json that contains an event with id s-x and status Postponed
        var html = new BbcHtmlBuilder()
            .WithLeague("LeagueJ")
            .AddGame(g => g.WithEventId("s-x").WithHome("PHome").WithAway("PAway"))
            .BuildPage();

        var mockParser = new Mock<IBbcJsonParser>();
        mockParser.Setup(p => p.BuildEventStatusMapStreaming(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(new Dictionary<string, (string periodLabel, string status, string statusComment)>
            {
                ["s-x"] = ("Postponed", "Postponed", string.Empty)
            });

        var f = CreateParser(mockParser.Object).ParseHtml(html).FirstOrDefault();
        Assert.NotNull(f);
        Assert.Equal("Postponed", f.Status);
        Assert.False(f.IsInProgress);
    }

    [Fact]
    public void ParseHtml_ParsesManyGames_NoCrash()
    {
        var builder = new BbcHtmlBuilder().WithLeague("Championship");
        for (var i = 0; i < 50; i++)
            builder.AddGame(g =>
                g.WithEventId($"s-{i}").WithHome($"Home{i}").WithAway($"Away{i}").WithScore(i % 3, (i + 1) % 3)
                    .WithProgressText(i % 2 == 0 ? "Live" : string.Empty));
        var html = builder.BuildPage();
        var fixtures = CreateParser().ParseHtml(html);
        Assert.NotNull(fixtures);
        Assert.Equal(50, fixtures.Count);
    }

    [Fact]
    public void ParseHtml_NoProgress_EmptyStatus()
    {
        var html = new BbcHtmlBuilder()
            .WithLeague("LeagueY")
            .AddGame(g => g.WithEventId("s-4").WithHome("NoProgHome").WithAway("NoProgAway"))
            .BuildPage();

        var f = CreateParser().ParseHtml(html).FirstOrDefault();
        Assert.NotNull(f);
        Assert.Equal(string.Empty, f.Status);
        Assert.False(f.HasProgress);
    }

    [Fact]
    public void ParseHtml_ParsesInExtraTime_ET_NotFinished()
    {
        var html = new BbcHtmlBuilder()
            .WithLeague("Premier League")
            .AddGame(g =>
                g.WithEventId("s-et1").WithHome("Newcastle").WithAway("Bournemouth").WithScore(2, 2)
                    .WithInExtraTime(92))
            .BuildPage();

        var f = CreateParser().ParseHtml(html).FirstOrDefault();
        Assert.NotNull(f);
        Assert.Equal("ET", f.Status);
        Assert.False(f.IsFinished);
        Assert.True(f.IsInProgress);
    }

    [Fact]
    public void BbcHtmlBuilder_Writes_ExtraTime_Markup()
    {
        var homeBadge =
            "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/newcastle-united.7daf913814.svg";
        var awayBadge =
            "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/afc-bournemouth.3e0ae7da8e.svg";

        var html = new BbcHtmlBuilder()
            .WithLeague("Premier League")
            .AddGame(g => g
                .WithEventId("s-a1pnbmnmt46a1y19g7jtidyj8")
                .WithHome("Newcastle")
                .WithAway("AFC Bournemouth")
                .WithScore(2, 2)
                .WithHomeBadge(homeBadge)
                .WithAwayBadge(awayBadge)
                .WithInExtraTime(100))
            .BuildPage();

        // Parse the HTML using the real parser and verify fields round-trip
        var fixtures = CreateParser().ParseHtml(html);
        var f = fixtures.FirstOrDefault(x =>
            x.Home.Contains("Newcastle", StringComparison.OrdinalIgnoreCase) &&
            x.Away.Contains("Bournemouth", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(f);

        // Status should be ET and game not finished but in progress
        Assert.Equal("ET", f.Status);
        Assert.False(f.IsFinished);
        Assert.True(f.IsInProgress);

        // Minute should be parsed as 100
        Assert.True(f.Minute.HasValue);
        Assert.Equal(100, f.Minute.Value);

        // Scores should be preserved
        Assert.Equal(2, f.HomeScore);
        Assert.Equal(2, f.AwayScore);

        // Badges should be extracted
        Assert.Contains("newcastle-united.7daf913814.svg", f.HomeBadgeUrl);
        Assert.Contains("afc-bournemouth.3e0ae7da8e.svg", f.AwayBadgeUrl);

        // AfterExtraTime property should be false for in-progress ET
        Assert.False(f.AfterExtraTime);
    }

    [Fact]
    public void BbcHtmlBuilder_Writes_Penalties_InProgress_Markup()
    {
        var html = new BbcHtmlBuilder()
            .WithLeague("Cup")
            .AddGame(g => g
                .WithHome("Hull City")
                .WithAway("Blackburn Rovers")
                .WithScore(0, 0)
                .WithPenalties(3, 2))
            .BuildPage();

        Assert.Contains("Penalties Hull City 3 , Blackburn Rovers 2", html);
        Assert.Contains("Penalties 3-2", html);
    }

    [Fact]
    public void ParseHtml_PenaltiesInProgress_IsNotFinished()
    {
        var html = new BbcHtmlBuilder()
            .WithLeague("Cup")
            .AddGame(g => g
                .WithHome("Hull")
                .WithAway("Blackburn")
                .WithScore(2, 2)
                // Simulating in-progress penalties
                .WithPenalties(3, 2))
            .BuildPage();

        var f = CreateParser().ParseHtml(html).FirstOrDefault();
        Assert.NotNull(f);

        // It should capture the "Penalties X-Y" as the status or just "Penalties" depending on implementation
        // Assuming the parser extracts the text from StyledPeriod
        Assert.Contains("Penalties", f.Status);

        Assert.True(f.IsInProgress, "Game should be considered in progress during penalties");
        Assert.False(f.IsFinished, "Game should not be considered finished during in-progress penalties");

        Assert.Equal(2, f.HomeScore);
        Assert.Equal(2, f.AwayScore);
    }

    [Fact]
    public void ParseHtml_ExtractsTeamLogos_Lazio_Como()
    {
        // Real BBC Sport HTML structure for Lazio vs Como
        // Como uses .webp format for their badge
        var html = GetResxValue("ParseHtml_ExtractsTeamLogos_Lazio_Como");

        var fixtures = CreateParser().ParseHtml(html);
        var f = fixtures.FirstOrDefault(x => x.Home == "Lazio" && x.Away == "Como");

        Assert.NotNull(f);
        Assert.Equal("Lazio", f.Home);
        Assert.Equal("Como", f.Away);
        Assert.Equal("Serie A", f.League);

        // Verify team logos are extracted correctly - .svg for Lazio, .webp for Como
        Assert.Equal("https://static.files.bbci.co.uk/core/website/assets/static/sport/football/lazio.8fc1f19371.svg",
            f.HomeBadgeUrl);
        Assert.Equal("https://static.files.bbci.co.uk/core/website/assets/static/sport/football/como.57ce7c985f.webp",
            f.AwayBadgeUrl);
    }

    [Fact]
    public void ParseHtml_ExtractsTeamLogos_WithBuilder()
    {
        // Test using BbcHtmlBuilder with explicit badge URLs
        var lazioLogo =
            "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/lazio.8fc1f19371.svg";
        var comoLogo = "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/como.57ce7c985f.svg";

        var html = new BbcHtmlBuilder()
            .WithLeague("Serie A")
            .AddGame(g => g
                .WithEventId("s-test-logo")
                .WithHome("Lazio")
                .WithAway("Como")
                .WithHomeBadge(lazioLogo)
                .WithAwayBadge(comoLogo))
            .BuildPage();

        var fixtures = CreateParser().ParseHtml(html);
        var f = fixtures.FirstOrDefault(x => x.Home == "Lazio" && x.Away == "Como");

        Assert.NotNull(f);

        // Verify exact badge URLs are preserved
        Assert.Contains("lazio.8fc1f19371.svg", f.HomeBadgeUrl);
        Assert.Contains("como.57ce7c985f.svg", f.AwayBadgeUrl);

        // Verify full URLs
        Assert.Equal(lazioLogo, f.HomeBadgeUrl);
        Assert.Equal(comoLogo, f.AwayBadgeUrl);
    }

    [Fact]
    public void ParseHtml_PlaceholderBadges_ReturnsEmpty()
    {
        // Test that placeholder badges are filtered out (both badges become empty)
        var html = new BbcHtmlBuilder()
            .WithLeague("Premier League")
            .AddGame(g => g
                .WithEventId("s-placeholder")
                .WithHome("Team A")
                .WithAway("Team B")
                .WithHomeBadge("https://example.com/placeholder.svg")
                .WithAwayBadge("https://example.com/real-badge.svg"))
            .BuildPage();

        var fixtures = CreateParser().ParseHtml(html);
        var f = fixtures.FirstOrDefault();

        Assert.NotNull(f);

        // If either badge is a placeholder, both should be empty
        Assert.True(string.IsNullOrEmpty(f.HomeBadgeUrl), "Home badge should be empty when placeholder detected");
        Assert.True(string.IsNullOrEmpty(f.AwayBadgeUrl), "Away badge should be empty when placeholder detected");
    }

    [Fact]
    public void ParseHtml_MissingOneBadge_ReturnsBothEmpty()
    {
        // Test that if one badge is missing, both are set to empty
        var html = new BbcHtmlBuilder()
            .WithLeague("La Liga")
            .AddGame(g => g
                .WithEventId("s-missing-badge")
                .WithHome("Barcelona")
                .WithAway("Real Madrid")
                .WithHomeBadge("https://example.com/barcelona.svg")
                // Away badge intentionally not set
                .WithAwayBadge(string.Empty))
            .BuildPage();

        var fixtures = CreateParser().ParseHtml(html);
        var f = fixtures.FirstOrDefault();

        Assert.NotNull(f);

        // If one badge is missing, both should be empty per parser logic
        Assert.True(string.IsNullOrEmpty(f.HomeBadgeUrl), "Home badge should be empty when away badge is missing");
        Assert.True(string.IsNullOrEmpty(f.AwayBadgeUrl), "Away badge should be empty");
    }

    [Fact]
    public void ParseHtml_FullTimeFixture_ShouldBeMarkedAsFinished()
    {
        // Test the exact HTML structure from the BBC fixture provided
        var html = new BbcHtmlBuilder()
            .WithLeague("Important Games")
            .AddGame(g => g
                .WithEventId("s-5wmfkj6y4bbcpfbso62fcqoes")
                .WithHome("Inter Milan")
                .WithAway("Arsenal")
                .WithScore(1, 3)
                .WithProgressText("Full time"))
            .BuildPage();

        var fixtures = CreateParser().ParseHtml(html);
        var f = fixtures.FirstOrDefault(x => x.Home.Contains("Inter Milan", StringComparison.OrdinalIgnoreCase));

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
        var html = GetResxValue("RawFullTimeAggregateGame");

        var fixtures = CreateParser().ParseHtml(html);
        var f = fixtures.FirstOrDefault();

        Assert.NotNull(f);
        Assert.Equal("FT", f.Status);
        Assert.True(f.IsFinished);
        Assert.False(f.IsInProgress);
        Assert.Equal(1, f.HomeScore);
        Assert.Equal(0, f.AwayScore);
    }

    [Fact]
    public void ParseHtml_WithAetPenaltiesFixture_IsCorrect()
    {
        var html = GetResxValue("RawAetPenaltiesGame");
        var fixtures = CreateParser().ParseHtml(html);

        var f = fixtures.FirstOrDefault();

        Assert.NotNull(f);
        Assert.Equal("AET", f.Status);
        Assert.True(f.IsFinished);
        Assert.False(f.IsInProgress);
        Assert.Equal(1, f.HomeScore);
        Assert.Equal(1, f.AwayScore);
        Assert.Equal(4, f.PenaltyWinnerGoals);
        Assert.Equal(2, f.PenaltyLoserGoals);
        Assert.Equal("Leeds United", f.PenaltyWinner);
    }

    [Fact]
    public void ParseHtml_FullTimeWithFTAbbreviation_ShouldBeMarkedAsFinished()
    {
        // Test that "FT" abbreviation is correctly recognized
        var html = new BbcHtmlBuilder()
            .WithLeague("Premier League")
            .AddGame(g => g
                .WithEventId("s-ft-test")
                .WithHome("Liverpool")
                .WithAway("Manchester United")
                .WithScore(2, 1)
                .WithProgressText("FT"))
            .BuildPage();

        var fixtures = CreateParser().ParseHtml(html);
        var f = fixtures.FirstOrDefault();

        Assert.NotNull(f);
        Assert.Equal("FT", f.Status);
        Assert.True(f.IsFinished, "Fixture with FT status should be marked as finished");
    }

    [Fact]
    public void ParseHtml_FullTimeVsInProgress_StatusDifference()
    {
        // Test that full-time and in-progress fixtures are correctly differentiated
        var html = new BbcHtmlBuilder()
            .WithLeague("Championship")
            .AddGame(g => g
                .WithEventId("s-ft-game")
                .WithHome("Team A")
                .WithAway("Team B")
                .WithScore(2, 0)
                .WithProgressText("Full time"))
            .AddGame(g => g
                .WithEventId("s-live-game")
                .WithHome("Team C")
                .WithAway("Team D")
                .WithScore(1, 1)
                .WithProgressText("67'"))
            .BuildPage();

        var fixtures = CreateParser().ParseHtml(html);
        var ftFixture = fixtures.FirstOrDefault(x => x.Home == "Team A");
        var liveFixture = fixtures.FirstOrDefault(x => x.Home == "Team C");

        Assert.NotNull(ftFixture);
        Assert.NotNull(liveFixture);

        // Full-time fixture
        Assert.Equal("FT", ftFixture.Status);
        Assert.True(ftFixture.IsFinished);
        Assert.False(ftFixture.IsInProgress);

        // In-progress fixture
        Assert.Equal("67'", liveFixture.Status);
        Assert.False(liveFixture.IsFinished);
        Assert.True(liveFixture.IsInProgress);
    }

    [Fact]
    public void BbcHtmlBuilder_FullTimeGame_ProducesCorrectMarkup()
    {
        // Arrange - Create the fixture using BbcHtmlBuilder
        var interMilanBadge =
            "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/inter-milan.209b8285b0.svg";
        var arsenalBadge =
            "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/arsenal.5be7ff54ce.svg";

        var html = new BbcHtmlBuilder()
            .WithLeague("Important Games")
            .AddGame(g => g
                .WithEventId("s-5wmfkj6y4bbcpfbso62fcqoes")
                .WithHome("Inter Milan")
                .WithAway("Arsenal")
                .WithScore(1, 3)
                .WithHomeBadge(interMilanBadge)
                .WithAwayBadge(arsenalBadge)
                .WithProgressText("Full time"))
            .BuildPage();

        // Assert - Verify all key markup elements are present

        // Event ID
        Assert.Contains("data-event-id=\"s-5wmfkj6y4bbcpfbso62fcqoes\"", html);

        // Team names (in various span formats)
        Assert.Contains("Inter Milan", html);
        Assert.Contains("Arsenal", html);

        // Badge URLs
        Assert.Contains(interMilanBadge, html);
        Assert.Contains(arsenalBadge, html);

        // Score elements
        Assert.Contains(">1<", html); // Home score
        Assert.Contains(">3<", html); // Away score

        // Full-time status text (visually-hidden)
        Assert.Contains("Full time", html);
    }

    [Fact]
    public void BbcHtmlBuilder_FullTimeGame_ParsedCorrectly()
    {
        // Arrange - Create and parse the fixture
        var interMilanBadge =
            "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/inter-milan.209b8285b0.svg";
        var arsenalBadge =
            "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/arsenal.5be7ff54ce.svg";

        var html = new BbcHtmlBuilder()
            .WithLeague("Important Games")
            .AddGame(g => g
                .WithEventId("s-5wmfkj6y4bbcpfbso62fcqoes")
                .WithHome("Inter Milan")
                .WithAway("Arsenal")
                .WithScore(1, 3)
                .WithHomeBadge(interMilanBadge)
                .WithAwayBadge(arsenalBadge)
                .WithProgressText("Full time"))
            .BuildPage();

        // Act - Parse the generated HTML
        var fixtures = CreateParser().ParseHtml(html);
        var fixture = fixtures.FirstOrDefault(x => x.Home == "Inter Milan" && x.Away == "Arsenal");

        // Assert - Verify all parsed values
        Assert.NotNull(fixture);
        Assert.Equal("Inter Milan", fixture.Home);
        Assert.Equal("Arsenal", fixture.Away);
        Assert.Equal("Important Games", fixture.League);
        Assert.Equal(1, fixture.HomeScore);
        Assert.Equal(3, fixture.AwayScore);
        Assert.Equal("FT", fixture.Status);
        Assert.True(fixture.IsFinished, "Full-time fixture should be marked as finished");
        Assert.False(fixture.IsInProgress, "Full-time fixture should not be in progress");
        Assert.Contains("inter-milan.209b8285b0.svg", fixture.HomeBadgeUrl);
        Assert.Contains("arsenal.5be7ff54ce.svg", fixture.AwayBadgeUrl);
    }

    [Fact]
    public void ParseHtml_ParsesRawHtmlFragment_FullTimeGame()
    {
        var html = GetResxValue("RawFullTimeGame");

        var fixtures = CreateParser().ParseHtml(html);
        var f = fixtures.FirstOrDefault(x => x.Home.Contains("Inter Milan") && x.Away.Contains("Arsenal"));
        Assert.NotNull(f);
        Assert.Equal("Inter Milan", f.Home);
        Assert.Equal("Arsenal", f.Away);
        Assert.Equal(1, f.HomeScore);
        Assert.Equal(3, f.AwayScore);
        Assert.Contains("inter-milan.209b8285b0.svg", f.HomeBadgeUrl);
        Assert.Contains("arsenal.5be7ff54ce.svg", f.AwayBadgeUrl);
        Assert.True(f.IsFinished);
        Assert.Equal("FT", f.Status);
    }

    [Fact]
    public void ParseHtml_ComplexStatusText_ParsesMinutesAndSpecials()
    {
        // 90+3 injury time
        var html = new BbcHtmlBuilder()
            .WithLeague("LeagueX")
            .AddGame(g =>
                g.WithEventId("s-inj").WithHome("InjTeam").WithAway("Opp").WithScore(1, 1).WithProgressText("90+3"))
            .AddGame(g =>
                g.WithEventId("s-et").WithHome("ETTeam").WithAway("Opp").WithScore(2, 2).WithProgressText("100' ET"))
            .AddGame(g =>
                g.WithEventId("s-pen").WithHome("PenTeam").WithAway("Opp").WithScore(0, 0)
                    .WithProgressText("Penalties 5-4"))
            .BuildPage();

        var fixtures = CreateParser().ParseHtml(html);
        var inj = fixtures.FirstOrDefault(x => x.Home == "InjTeam");
        var et = fixtures.FirstOrDefault(x => x.Home == "ETTeam");
        var pen = fixtures.FirstOrDefault(x => x.Home == "PenTeam");

        Assert.NotNull(inj);
        Assert.NotNull(et);
        Assert.NotNull(pen);

        // Injury + time should parse to encoded minute 9003 and be in-progress
        Assert.True(inj.Minute.HasValue);
        Assert.Equal(9003, inj.Minute.Value);
        Assert.True(inj.IsInProgress);

        // ET text should mark ET and preserve minute
        Assert.Equal("ET", et.Status);
        Assert.True(et.Minute.HasValue);
        Assert.Equal(100, et.Minute.Value);
        Assert.True(et.IsInProgress);

        // Penalties should be considered in-progress penalties status
        Assert.Contains("Penalties", pen.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(pen.IsInProgress);
    }

    [Fact]
    public void ParseHtml_SionVsBasel_NotInProgress()
    {
        var html = GetResxValue("RawInProgressIssue");

        var fixtures = CreateParser().ParseHtml(html);
        var f = fixtures.FirstOrDefault(x => x.Home == "Sion" && x.Away == "Basel");

        Assert.NotNull(f);
        Assert.False(f.IsInProgress);
        Assert.False(f.HasProgress);
        Assert.Equal(string.Empty, f.Status);
    }
}