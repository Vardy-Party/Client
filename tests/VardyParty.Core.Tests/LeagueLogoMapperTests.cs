using VardyParty.Models;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests
{
    public class LeagueLogoMapperTests
    {
        [Theory]
        [InlineData("Premier League", "premier-league-logo")]
        [InlineData("premier league", "premier-league-logo")]
        [InlineData("La Liga", "la-liga-2023-logo")]
        [InlineData("Serie A", "serie-a-logo")]
        [InlineData("Copa del Rey", "serie-a-logo")]
        [InlineData("Spanish Supercopa", "Supercopa_de_Espa~na-logo")]
        [InlineData("Unknown League", "")] // should return empty for unknown leagues
        public void GetLogoForLeague_ReturnsExpectedPathFragment(string league, string expectedFragment)
        {
            var game = new Game { League = league };
            var path = LeagueLogoMapper.GetLogoForLeague(game);
            if (string.IsNullOrEmpty(expectedFragment))
            {
                Assert.True(string.IsNullOrEmpty(path));
            }
            else
            {
                Assert.False(string.IsNullOrEmpty(path));
                Assert.Contains(expectedFragment, path, System.StringComparison.OrdinalIgnoreCase);
            }
        }

        [Theory]
        [InlineData("Premier League", "premier-league-logo")]
        [InlineData("premier league", "premier-league-logo")]
        [InlineData("La Liga", "la-liga-2023-logo")]
        [InlineData("Serie A", "serie-a-logo")]
        [InlineData("Copa del Rey", "serie-a-logo")]
        [InlineData("Spanish Supercopa", "Supercopa_de_Espa~na-logo")]
        [InlineData("Unknown League", "")] // should return empty for unknown leagues
        public void GetLogoForLeague_ReturnsExpectedPathFragmentWithBBCLeague(string league, string expectedFragment)
        {
            var game = new Game { BBCLeague = league };
            var path = LeagueLogoMapper.GetLogoForLeague(game);
            if (string.IsNullOrEmpty(expectedFragment))
            {
                Assert.True(string.IsNullOrEmpty(path));
            }
            else
            {
                Assert.False(string.IsNullOrEmpty(path));
                Assert.Contains(expectedFragment, path, System.StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
