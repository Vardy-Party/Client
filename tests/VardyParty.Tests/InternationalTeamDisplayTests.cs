using System.Linq;
using AutoFixture;
using VardyParty.Models;
using Xunit;

namespace VardyParty.Tests;

public class InternationalTeamDisplayTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void TickerSeparator_UsesFootballEmoji()
    {
        // Arrange
        const string expected = "   \u26bd   ";

        // Act
        var separator = InternationalTeamDisplay.TickerSeparator;

        // Assert
        Assert.Equal(expected, separator);
    }

    [Fact]
    public void IsInternationalMatch_ClubLeague_ReturnsFalse()
    {
        // Arrange
        const string league = "League Alpha";
        const string home = "Home United";
        const string away = "Away City";

        // Act
        var isInternational = InternationalTeamDisplay.IsInternationalMatch(league, home, away);

        // Assert
        Assert.False(isInternational);
    }

    [Fact]
    public void FormatTeamName_UnknownInternationalTeam_ReturnsPlainName()
    {
        // Arrange
        const string teamName = "Home United";

        // Act
        var formatted = InternationalTeamDisplay.FormatTeamName(teamName, international: true);

        // Assert
        Assert.Equal("Home United", formatted);
    }

    [Fact]
    public void FormatTeamName_ClubTeam_NoFlag()
    {
        // Arrange
        var game = _fixture.Build<Game>()
            .With(g => g.League, "League Alpha")
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.BBCLeague, string.Empty)
            .With(g => g.BBCHome, string.Empty)
            .With(g => g.BBCAway, string.Empty)
            .Create();

        // Act
        var isInternational = InternationalTeamDisplay.IsInternationalGame(game);
        var formatted = InternationalTeamDisplay.FormatTeamName("Home United", international: false);

        // Assert
        Assert.False(isInternational);
        Assert.Equal("Home United", formatted);
    }

    [Fact]
    public void TryGetIsoCode_UnknownTeam_ReturnsFalse()
    {
        // Arrange
        const string teamName = "Home United";

        // Act
        var found = InternationalTeamDisplay.TryGetIsoCode(teamName, out var iso);

        // Assert
        Assert.False(found);
        Assert.Equal(string.Empty, iso);
    }

    [Fact]
    public void GetFlagImageUrl_ReturnsFlagCdnUrl()
    {
        // Arrange
        const string iso = "zz";

        // Act
        var url = InternationalTeamDisplay.GetFlagImageUrl(iso);

        // Assert
        Assert.Equal("https://flagcdn.com/16x12/zz.png", url);
    }

    [Fact]
    public void TeamParts_UnknownInternationalTeam_TextOnly()
    {
        // Arrange
        const string teamName = "Home United";

        // Act
        var parts = InternationalTeamDisplay.TeamParts(teamName, international: true).ToList();

        // Assert
        Assert.Single(parts);
        Assert.Equal("Home United", parts[0].Text);
        Assert.Null(parts[0].FlagImageUrl);
    }

    [Fact]
    public void TeamParts_ClubTeam_TextOnly()
    {
        // Arrange
        const string teamName = "Away City";

        // Act
        var parts = InternationalTeamDisplay.TeamParts(teamName, international: false).ToList();

        // Assert
        Assert.Single(parts);
        Assert.Equal("Away City", parts[0].Text);
        Assert.Null(parts[0].FlagImageUrl);
    }
}
