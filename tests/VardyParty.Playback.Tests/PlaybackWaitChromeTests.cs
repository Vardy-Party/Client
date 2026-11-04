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
        // Arrange
        // Act
        var show = PlaybackWaitChrome.ShouldShow(status, isPreparing, chromeSuppressed: false, failoverAgeMs: 0);

        // Assert
        Assert.Equal(expected, show);
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
        // Arrange
        // Act
        var show = PlaybackWaitChrome.ShouldShow(status, isPreparing, chromeSuppressed: true, failoverAgeMs: 0);

        // Assert
        Assert.False(show);
    }

    [Theory]
    [InlineData(PlaybackStatus.Idle)]
    [InlineData(PlaybackStatus.Error)]
    public void ShouldShow_IdleAndError_AreBoundedByTheFailoverGrace(PlaybackStatus status)
    {
        // Arrange
        // Act
        var withinGrace = PlaybackWaitChrome.ShouldShow(status, false, false, Grace - 1);
        var atGrace = PlaybackWaitChrome.ShouldShow(status, false, false, Grace);
        var pastGrace = PlaybackWaitChrome.ShouldShow(status, false, false, Grace * 10);

        // Assert
        Assert.True(withinGrace);
        Assert.False(atGrace);
        Assert.False(pastGrace);
    }

    [Theory]
    [InlineData(PlaybackStatus.Idle)]
    [InlineData(PlaybackStatus.Error)]
    public void ShouldShow_IdleAndError_PastTheGrace_StillShowWhileAnAttachIsInFlight(PlaybackStatus status)
    {
        // Arrange
        // Act
        var show = PlaybackWaitChrome.ShouldShow(status, isPreparing: true, chromeSuppressed: false, failoverAgeMs: Grace * 10);

        // Assert
        Assert.True(show);
    }

    [Theory]
    [InlineData(PlaybackStatus.Idle)]
    [InlineData(PlaybackStatus.Error)]
    public void ShouldShow_IdleAndError_PastTheGrace_StillShowWhileAwaitingRetry(PlaybackStatus status)
    {
        // Arrange — fresh /play recovery: player stays Error, prepare has not started yet.
        // Act
        var show = PlaybackWaitChrome.ShouldShow(
            status, isPreparing: false, chromeSuppressed: false, failoverAgeMs: Grace * 10, awaitingRetry: true);

        // Assert
        Assert.True(show);
    }

    [Fact]
    public void ShouldShow_BufferingAndPlaying_AreNotBoundedByTheFailoverGrace()
    {
        // Arrange
        // Act
        var buffering = PlaybackWaitChrome.ShouldShow(PlaybackStatus.Buffering, false, false, Grace * 10);
        var playing = PlaybackWaitChrome.ShouldShow(PlaybackStatus.Playing, false, false, Grace * 10);

        // Assert
        Assert.True(buffering);
        Assert.False(playing);
    }
}
