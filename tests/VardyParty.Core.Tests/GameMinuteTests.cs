using VardyParty.Models;
using Xunit;

namespace VardyParty.Core.Tests;

public class GameMinuteTests
{
    [Theory]
    [InlineData("45+2'", 4502)]
    [InlineData("90+7'", 9007)]
    [InlineData("12'", 12)]
    public void MinuteFromStatus_ParsesEncoded(string status, int expected)
    {
        var g = new Game { StatusText = status };
        var minuteFromStatus = typeof(Game).GetProperty("MinuteFromStatus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(g);

        Assert.Equal(expected, minuteFromStatus);
    }

    [Fact]
    public void DisplayStatusText_FormatsEncodedProperly()
    {
        var g = new Game { IsInProgress = true, Minute = 9004 };
        var display = g.DisplayStatusText();
        Assert.Equal("90+4'", display);
    }

    [Fact]
    public void LiveMinuteForOrdering_SortsCorrectly()
    {
        var g1 = new Game { IsInProgress = true, Minute = 9004 };
        var g2 = new Game { IsInProgress = true, Minute = 70 };

        Assert.True(g1.LiveMinuteForOrdering > g2.LiveMinuteForOrdering);
    }
}
