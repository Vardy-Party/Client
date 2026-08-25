using AutoFixture;
using VardyParty.Models;
using Xunit;
using VardyParty.Catalog;

namespace VardyParty.Tests
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
    }
}
