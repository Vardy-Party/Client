using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using VardyParty.Catalog;

namespace VardyParty.Catalog.Tests
{
    public class BbcParserTests
    {
        private BbcHtmlParser CreateParser()
        {
            var logger = NullLogger<BbcHtmlParser>.Instance;
            var parser = new BbcJsonParser(NullLogger<BbcJsonParser>.Instance);
            return new BbcHtmlParser(logger, parser);
        }

        [Fact]
        public void Parse_FullTime_Score4_1()
        {
            // Arrange
            const string homeBadge = "https://badges.example.com/home-united.svg";
            const string awayBadge = "https://badges.example.com/away-city.svg";
            var html = new BbcHtmlBuilder()
                .WithLeague("League Alpha")
                .AddGame(g => g
                    .WithEventId("s-ft-1")
                    .WithHome("Home United")
                    .WithAway("Away City")
                    .WithScore(4, 1)
                    .WithProgressText("FT")
                    .WithHomeBadge(homeBadge)
                    .WithAwayBadge(awayBadge))
                .BuildPage();
            var sut = CreateParser();

            // Act
            var fixtures = sut.ParseHtml(html);

            // Assert
            var fixture = Assert.Single(fixtures);
            Assert.Equal("Home United", fixture.Home);
            Assert.Equal("Away City", fixture.Away);
            Assert.Equal(4, fixture.HomeScore);
            Assert.Equal(1, fixture.AwayScore);
            Assert.True(fixture.IsFinished, "Should be finished");
            Assert.Equal("FT", fixture.Status);
            Assert.Equal(homeBadge, fixture.HomeBadgeUrl);
            Assert.Equal(awayBadge, fixture.AwayBadgeUrl);
        }

        [Fact]
        public void Parse_Postponed_HasNoScores()
        {
            // Arrange
            var html = new BbcHtmlBuilder()
                .WithLeague("League Beta")
                .AddGame(g => g
                    .WithEventId("s-pp-1")
                    .WithHome("Home United")
                    .WithAway("Away City")
                    .WithProgressText("Match Postponed"))
                .BuildPage();
            var sut = CreateParser();

            // Act
            var fixtures = sut.ParseHtml(html);

            // Assert
            var fixture = Assert.Single(fixtures);
            Assert.Equal("Home United", fixture.Home);
            Assert.Equal("Away City", fixture.Away);
            Assert.Null(fixture.HomeScore);
            Assert.Null(fixture.AwayScore);
            Assert.False(fixture.IsFinished);
            Assert.False(fixture.IsInProgress);
            Assert.Equal("Postponed", fixture.Status);
        }

        [Fact]
        public void Parse_Aet_Finished()
        {
            // Arrange
            var html = new BbcHtmlBuilder()
                .WithLeague("League Gamma")
                .AddGame(g => g
                    .WithEventId("s-aet-1")
                    .WithHome("Home United")
                    .WithAway("Away City")
                    .WithScore(3, 4)
                    .WithProgressText("AET")
                    .WithAfterExtraTime())
                .BuildPage();
            var sut = CreateParser();

            // Act
            var fixtures = sut.ParseHtml(html);

            // Assert
            var fixture = Assert.Single(fixtures);
            Assert.Equal("Home United", fixture.Home);
            Assert.Equal("Away City", fixture.Away);
            Assert.Equal(3, fixture.HomeScore);
            Assert.Equal(4, fixture.AwayScore);
            Assert.True(fixture.IsFinished, "Should be IsFinished=true for AET");
            Assert.False(fixture.IsInProgress, "Should be IsInProgress=false for AET");
            Assert.Equal("AET", fixture.Status);
        }
    }
}
