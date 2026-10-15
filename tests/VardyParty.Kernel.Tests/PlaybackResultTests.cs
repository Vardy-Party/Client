using System.Text.Json;
using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

public class PlaybackResultTests
{
    [Theory]
    [InlineData(PlaybackCloseReasons.UserClosed, true, false, true)]
    [InlineData(PlaybackCloseReasons.PlayerClosed, false, true, true)]
    [InlineData("User closed video player", true, false, true)]
    [InlineData("user closed", true, false, true)]
    [InlineData("Stream ended.", false, false, false)]
    [InlineData("Playback error: closed unexpectedly", false, false, false)]
    [InlineData("", false, false, false)]
    public void CloseClassification_MatchesTheViewerActuallyLeaving(string message, bool userClosed, bool playerClosed, bool viewerLeft)
    {
        var result = PlaybackResult.Completed(message);

        Assert.Equal(userClosed, result.IsUserClosed);
        Assert.Equal(playerClosed, result.IsPlayerClosed);
        Assert.Equal(viewerLeft, result.ViewerLeft);
    }

    [Fact]
    public void CloseClassification_ToleratesANullMessage()
    {
        var result = new PlaybackResult { Message = null! };

        Assert.False(result.ViewerLeft);
    }

    [Fact]
    public void CloseClassification_IsNotSerialised()
    {
        var json = JsonSerializer.Serialize(PlaybackResult.Completed(PlaybackCloseReasons.UserClosed));

        Assert.DoesNotContain("ViewerLeft", json);
        Assert.DoesNotContain("IsUserClosed", json);
        Assert.DoesNotContain("IsPlayerClosed", json);
    }
}