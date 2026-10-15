using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

public class PlaybackWaitChromeTests
{
    private const long Grace = PlaybackWaitChrome.FailoverGraceMs;

    // (status x isPreparing), not closing, failover just started.
    [Theory]
    [InlineData(PlaybackStatus.Buffering, false, true)]
    [InlineData(PlaybackStatus.Buffering, true, true)]
    [InlineData(PlaybackStatus.Playing, false, false)]   // healthy playback: no spinner, ever
    [InlineData(PlaybackStatus.Playing, true, true)]     // attach in flight
    [InlineData(PlaybackStatus.Idle, false, true)]       // failover attaches the next URL a beat later
    [InlineData(PlaybackStatus.Idle, true, true)]
    [InlineData(PlaybackStatus.Error, false, true)]
    [InlineData(PlaybackStatus.Error, true, true)]
    [InlineData(PlaybackStatus.Ended, false, false)]
    [InlineData(PlaybackStatus.Ended, true, false)]
    public void ShouldShow_StatusByPreparing(PlaybackStatus status, bool isPreparing, bool expected)
    {
        Assert.Equal(expected, PlaybackWaitChrome.ShouldShow(status, isPreparing, isClosing: false, failoverAgeMs: 0));
    }

    [Theory]
    [InlineData(PlaybackStatus.Buffering, true)]
    [InlineData(PlaybackStatus.Playing, true)]
    [InlineData(PlaybackStatus.Idle, true)]
    [InlineData(PlaybackStatus.Error, true)]
    [InlineData(PlaybackStatus.Ended, true)]
    [InlineData(PlaybackStatus.Buffering, false)]
    [InlineData(PlaybackStatus.Idle, false)]
    public void ShouldShow_WhileClosing_IsAlwaysHidden(PlaybackStatus status, bool isPreparing)
    {
        Assert.False(PlaybackWaitChrome.ShouldShow(status, isPreparing, isClosing: true, failoverAgeMs: 0));
    }

    [Theory]
    [InlineData(PlaybackStatus.Idle)]
    [InlineData(PlaybackStatus.Error)]
    public void ShouldShow_IdleAndError_AreBoundedByTheFailoverGrace(PlaybackStatus status)
    {
        Assert.True(PlaybackWaitChrome.ShouldShow(status, false, false, Grace - 1));
        Assert.False(PlaybackWaitChrome.ShouldShow(status, false, false, Grace));
        Assert.False(PlaybackWaitChrome.ShouldShow(status, false, false, Grace * 10));
    }

    [Theory]
    [InlineData(PlaybackStatus.Idle)]
    [InlineData(PlaybackStatus.Error)]
    public void ShouldShow_IdleAndError_PastTheGrace_StillShowWhileAnAttachIsInFlight(PlaybackStatus status)
    {
        Assert.True(PlaybackWaitChrome.ShouldShow(status, isPreparing: true, isClosing: false, failoverAgeMs: Grace * 10));
    }

    [Fact]
    public void ShouldShow_BufferingAndPlaying_AreNotBoundedByTheFailoverGrace()
    {
        Assert.True(PlaybackWaitChrome.ShouldShow(PlaybackStatus.Buffering, false, false, Grace * 10));
        Assert.False(PlaybackWaitChrome.ShouldShow(PlaybackStatus.Playing, false, false, Grace * 10));
    }
}