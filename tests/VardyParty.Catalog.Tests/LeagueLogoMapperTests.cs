using AutoFixture;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Catalog;
using VardyParty.TestSupport;

namespace VardyParty.Catalog.Tests
{
    public class LeagueLogoMapperTests
    {
        private readonly IFixture _fixture = AutoMoqFixture.Create();

        [Theory]
        [InlineData("League Alpha")]
        [InlineData("Unknown League")]
        [InlineData("")]
        public void GetLogoForLeague_UnknownLeague_ReturnsEmpty(string league)
        {
            // Arrange
            var game = _fixture.Build<Game>()
                .With(g => g.League, league)
                .With(g => g.BBCLeague, string.Empty)
                .Create();

            // Act
            var path = LeagueLogoMapper.GetLogoForLeague(game);

            // Assert
            Assert.True(string.IsNullOrEmpty(path));
        }

        [Theory]
        [InlineData("League Alpha")]
        [InlineData("Unknown League")]
        [InlineData("")]
        public void GetLogoForLeague_UnknownBbcLeague_ReturnsEmpty(string league)
        {
            // Arrange
            var game = _fixture.Build<Game>()
                .With(g => g.League, string.Empty)
                .With(g => g.BBCLeague, league)
                .Create();

            // Act
            var path = LeagueLogoMapper.GetLogoForLeague(game);

            // Assert
            Assert.True(string.IsNullOrEmpty(path));
        }

        [Theory]
        [InlineData("UEFA Nations League", "/images/leagues/uefa-nations-league.png")]
        [InlineData("Concacaf Nations League", "/images/leagues/concacaf-nations-league-2026.svg")]
        [InlineData("CONCACAF Nations League", "/images/leagues/concacaf-nations-league-2026.svg")]
        [InlineData("UEFA Champions League", "/images/leagues/uefa-champions-league-logo-brandlogos.net_iyyz8y0dw.svg")]
        public void GetLogoForLeague_NationsAndChampionsLeagues_ReturnsExpectedPath(string league, string expected)
        {
            var game = _fixture.Build<Game>()
                .With(g => g.League, league)
                .With(g => g.BBCLeague, string.Empty)
                .Create();

            var path = LeagueLogoMapper.GetLogoForLeague(game);

            Assert.Equal(expected, path);
        }

        [Fact]
        public void GetLogoForLeague_BareNationsLeague_ReturnsEmpty()
        {
            var game = _fixture.Build<Game>()
                .With(g => g.League, "Nations League")
                .With(g => g.BBCLeague, string.Empty)
                .Create();

            var path = LeagueLogoMapper.GetLogoForLeague(game);

            Assert.True(string.IsNullOrEmpty(path));
        }
    }
}
