using System;
using VardyParty.Hosting;
using Xunit;

namespace VardyParty.Hosting.Tests;

/// <summary>
/// Rules learned from a live phone session: the phone gave up at 90s while
/// Chrome was still waiting, and a retry was rejected as "busy" so the
/// overlay vanished.
/// </summary>
public class RemoteComputeHttpTests
{
    [Fact]
    public void FindingStreamsTimeout_OutlastsTheSequentialPhoneChromeWaits()
    {
        // Chip 120s, then playlist 180s, then segments 60s. See PhonePathBudget.
        var chromeSequence = TimeSpan.FromSeconds(120 + 180 + 60);

        Assert.True(RemoteComputeHttp.FindingStreamsTimeout > chromeSequence);
        Assert.Equal(TimeSpan.FromSeconds(420), RemoteComputeHttp.FindingStreamsTimeout);
    }

    [Fact]
    public void BusyComputer_TellsTheGuestToTapTheGameAgain()
    {
        var message = RemoteComputeHttp.LocalServiceMessage(409, "compute host is busy");

        Assert.Equal(RemoteComputeHttp.StillFinding, message);
        Assert.Contains("Tap the game again", message, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherConflict_KeepsTheRemoteReason()
    {
        var message = RemoteComputeHttp.LocalServiceMessage(409, "pair is locked");

        Assert.Equal("pair is locked", message);
    }

    [Theory]
    [InlineData(403, null, "The remote PC refused this account")]
    [InlineData(404, "M3U8 not captured from MP page", "M3U8 not captured from MP page")]
    [InlineData(500, null, "The remote PC returned HTTP 500")]
    public void LocalServiceMessage_UsesTheBodyWhenTheComputerAnswers(int status, string? error, string expected)
    {
        Assert.Equal(expected, RemoteComputeHttp.LocalServiceMessage(status, error));
    }
}
