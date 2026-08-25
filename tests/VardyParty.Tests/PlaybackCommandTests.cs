using VardyParty.Playback;
using Xunit;

namespace VardyParty.Tests;

public class PlaybackCommandTests
{
    [Fact]
    public void UserNext_SwitchesPoolIndex_DoesNotRemove()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var cmd = PlaybackCommand.FromEffects(session.Handle(MediaEngineEvent.UserNext()));

        // Assert
        Assert.True(cmd.SwitchPoolToNext);
        Assert.False(cmd.RemoveCurrentFromPool);
        Assert.False(cmd.AttachCurrentAfterRemove);
        Assert.False(cmd.AttachIsRevert);
    }

    [Fact]
    public void EstablishedHardFail_AttachesCurrentAfterRemove_DoesNotSwitchIndex()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var cmd = PlaybackCommand.FromEffects(
            session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "decoder")));

        // Assert
        Assert.True(cmd.RemoveCurrentFromPool);
        Assert.True(cmd.AttachCurrentAfterRemove);
        Assert.False(cmd.SwitchPoolToNext);
        Assert.False(cmd.AttachIsRevert);
        Assert.True(cmd.ReportFailed);
    }

    [Fact]
    public void FailedSwitch_RevertsWithoutAdvancingPool()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://good.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        session.BeginAttach("http://bad.m3u8");

        // Act
        var cmd = PlaybackCommand.FromEffects(
            session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "switch fail")));

        // Assert
        Assert.True(cmd.AttachIsRevert);
        Assert.Equal("http://good.m3u8", cmd.AttachUrl);
        Assert.True(cmd.RemoveCurrentFromPool);
        Assert.False(cmd.SwitchPoolToNext);
        Assert.False(cmd.AttachCurrentAfterRemove);
    }

    [Fact]
    public void FailedStart_WithCache_RequestsFreshResolve()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://cached.m3u8", usedCachedUrl: true);

        // Act
        var cmd = PlaybackCommand.FromEffects(
            session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "403")));

        // Assert
        Assert.True(cmd.RetryFreshResolve);
        Assert.True(cmd.ClearResolvedUrl);
        Assert.False(cmd.SwitchPoolToNext);
        Assert.False(cmd.RemoveCurrentFromPool);
    }

    [Fact]
    public void Buffering_RaisesWithFlag()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        // Act
        var cmd = PlaybackCommand.FromEffects(session.Handle(MediaEngineEvent.Buffering(gen, true)));

        // Assert
        Assert.True(cmd.RaiseBuffering);
        Assert.True(cmd.IsBuffering);
    }

    [Fact]
    public void UserPrevious_SwitchesPoolIndex_DoesNotRemove()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var cmd = PlaybackCommand.FromEffects(session.Handle(MediaEngineEvent.UserPrevious()));

        // Assert
        Assert.True(cmd.SwitchPoolToPrevious);
        Assert.False(cmd.RemoveCurrentFromPool);
        Assert.False(cmd.SwitchPoolToNext);
    }

    [Fact]
    public void FailedStart_AttachesCurrentAfterRemove()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");

        // Act
        var cmd = PlaybackCommand.FromEffects(
            session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "start fail")));

        // Assert
        Assert.True(cmd.RemoveCurrentFromPool);
        Assert.True(cmd.AttachCurrentAfterRemove);
        Assert.False(cmd.SwitchPoolToNext);
    }

    [Fact]
    public void EstablishedHardFail_LastStream_ClosesWithoutAttach()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(1);
        session.BeginAttach("http://only.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        // Act
        var cmd = PlaybackCommand.FromEffects(
            session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "decoder")));

        // Assert
        Assert.True(cmd.CloseSession);
        Assert.True(cmd.RemoveCurrentFromPool);
        Assert.False(cmd.AttachCurrentAfterRemove);
        Assert.False(cmd.SwitchPoolToNext);
    }
}
