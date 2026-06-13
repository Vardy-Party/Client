using System.Linq;
using VardyParty.Models;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests;

public class InternationalTeamDisplayTests
{
    [Fact]
    public void TickerSeparator_UsesFootballEmoji()
    {
        Assert.Equal("   \u26bd   ", InternationalTeamDisplay.TickerSeparator);
    }

    [Fact]
    public void IsInternationalMatch_WorldCupLeague_ReturnsTrue()
    {
        Assert.True(InternationalTeamDisplay.IsInternationalMatch("FIFA World Cup", "USA", "Paraguay"));
    }

    [Fact]
    public void FormatTeamName_InternationalTeam_IncludesFlagEmoji()
    {
        var formatted = InternationalTeamDisplay.FormatTeamName("United States", international: true);
        Assert.StartsWith("\U0001F1FA\U0001F1F8", formatted);
        Assert.Contains("United States", formatted);
    }

    [Fact]
    public void FormatTeamName_ClubTeam_NoFlag()
    {
        var game = new Game { League = "Premier League", Home = "Arsenal", Away = "Chelsea" };
        Assert.False(InternationalTeamDisplay.IsInternationalGame(game));
        Assert.Equal("Arsenal", InternationalTeamDisplay.FormatTeamName("Arsenal", international: false));
    }

    [Fact]
    public void TryGetIsoCode_KnownTeam_ReturnsIso()
    {
        Assert.True(InternationalTeamDisplay.TryGetIsoCode("Qatar", out var iso));
        Assert.Equal("QA", iso);
    }

    [Fact]
    public void GetFlagImageUrl_ReturnsFlagCdnUrl()
    {
        Assert.Equal("https://flagcdn.com/16x12/qa.png", InternationalTeamDisplay.GetFlagImageUrl("QA"));
    }

    [Fact]
    public void TeamParts_InternationalTeam_IncludesFlagImageUrl()
    {
        var parts = InternationalTeamDisplay.TeamParts("Switzerland", international: true).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal("https://flagcdn.com/16x12/ch.png", parts[0].FlagImageUrl);
        Assert.Contains("Switzerland", parts[1].Text);
    }

    [Fact]
    public void TeamParts_ClubTeam_TextOnly()
    {
        var parts = InternationalTeamDisplay.TeamParts("Malaga", international: false).ToList();
        Assert.Single(parts);
        Assert.Equal("Malaga", parts[0].Text);
        Assert.Null(parts[0].FlagImageUrl);
    }
}
