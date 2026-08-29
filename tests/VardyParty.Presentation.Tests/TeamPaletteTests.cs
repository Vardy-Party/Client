using System.Text.RegularExpressions;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class TeamPaletteTests
{
    private static readonly Regex HexColor = new("^#[0-9A-F]{6}$");

    [Fact]
    public void GetColors_KnownTeam_ReturnsCuratedColours()
    {
        // Act
        var colors = TeamPalette.GetColors("Arsenal");

        // Assert
        Assert.Equal("#EF0107", colors.Primary);
        Assert.Equal("#023474", colors.Secondary);
    }

    [Fact]
    public void GetColors_KnownTeam_IsCaseInsensitive()
    {
        // Act & Assert
        Assert.Equal(TeamPalette.GetColors("Liverpool"), TeamPalette.GetColors("LIVERPOOL"));
    }

    [Theory]
    [InlineData("Everton FC", "Everton")]
    [InlineData("Celtic FC", "Celtic")]
    public void GetColors_StripsCommonSuffixes(string withSuffix, string bare)
    {
        // Act & Assert
        Assert.Equal(TeamPalette.GetColors(bare), TeamPalette.GetColors(withSuffix));
    }

    [Fact]
    public void GetColors_UnknownTeam_IsDeterministic()
    {
        // Act
        var first = TeamPalette.GetColors("Borehamwood Wanderers");
        var second = TeamPalette.GetColors("Borehamwood Wanderers");

        // Assert
        Assert.Equal(first, second);
    }

    [Fact]
    public void GetColors_UnknownTeams_GetDistinctHues()
    {
        // Act
        var one = TeamPalette.GetColors("Borehamwood Wanderers");
        var other = TeamPalette.GetColors("Chigwell Rovers");

        // Assert
        Assert.NotEqual(one.Primary, other.Primary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Nonexistent Team XI")]
    public void GetColors_AlwaysReturnsValidHex(string? name)
    {
        // Act
        var colors = TeamPalette.GetColors(name);

        // Assert
        Assert.Matches(HexColor, colors.Primary);
        Assert.Matches(HexColor, colors.Secondary);
    }
}
