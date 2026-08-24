using VardyParty.Playback;
using Xunit;

namespace VardyParty.Core.Tests;

public class PlaybackCommandTests
{
    [Fact]
    public void UserNext_SwitchesPoolIndex_DoesNotRemove()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        var cmd = PlaybackCommand.FromEffects(session.Handle(MediaEngineEvent.UserNext()));

        Assert.True(cmd.SwitchPoolToNext);
        Assert.False(cmd.RemoveCurrentFromPool);
        Assert.False(cmd.AttachCurrentAfterRemove);
        Assert.False(cmd.AttachIsRevert);
    }

    [Fact]
    public void EstablishedHardFail_AttachesCurrentAfterRemove_DoesNotSwitchIndex()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        var cmd = PlaybackCommand.FromEffects(
            session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "decoder")));

        Assert.True(cmd.RemoveCurrentFromPool);
        Assert.True(cmd.AttachCurrentAfterRemove);
        Assert.False(cmd.SwitchPoolToNext);
        Assert.False(cmd.AttachIsRevert);
        Assert.True(cmd.ReportFailed);
    }

    [Fact]
    public void FailedSwitch_RevertsWithoutAdvancingPool()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://good.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        session.BeginAttach("http://bad.m3u8");

        var cmd = PlaybackCommand.FromEffects(
            session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "switch fail")));

        Assert.True(cmd.AttachIsRevert);
        Assert.Equal("http://good.m3u8", cmd.AttachUrl);
        Assert.True(cmd.RemoveCurrentFromPool);
        Assert.False(cmd.SwitchPoolToNext);
        Assert.False(cmd.AttachCurrentAfterRemove);
    }

    [Fact]
    public void FailedStart_WithCache_RequestsFreshResolve()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://cached.m3u8", usedCachedUrl: true);

        var cmd = PlaybackCommand.FromEffects(
            session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "403")));

        Assert.True(cmd.RetryFreshResolve);
        Assert.True(cmd.ClearResolvedUrl);
        Assert.False(cmd.SwitchPoolToNext);
        Assert.False(cmd.RemoveCurrentFromPool);
    }

    [Fact]
    public void Buffering_RaisesWithFlag()
    {
        var session = new PlaybackSessionController();
        session.BeginAttach("http://a.m3u8");
        var gen = session.Snapshot.AttachGeneration;
        session.Handle(MediaEngineEvent.Ready(gen));

        var cmd = PlaybackCommand.FromEffects(session.Handle(MediaEngineEvent.Buffering(gen, true)));
        Assert.True(cmd.RaiseBuffering);
        Assert.True(cmd.IsBuffering);
    }

    [Fact]
    public void UserPrevious_SwitchesPoolIndex_DoesNotRemove()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));

        var cmd = PlaybackCommand.FromEffects(session.Handle(MediaEngineEvent.UserPrevious()));

        Assert.True(cmd.SwitchPoolToPrevious);
        Assert.False(cmd.RemoveCurrentFromPool);
        Assert.False(cmd.SwitchPoolToNext);
    }

    [Fact]
    public void FailedStart_AttachesCurrentAfterRemove()
    {
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8");

        var cmd = PlaybackCommand.FromEffects(
            session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "start fail")));

        Assert.True(cmd.RemoveCurrentFromPool);
        Assert.True(cmd.AttachCurrentAfterRemove);
        Assert.False(cmd.SwitchPoolToNext);
    }
}
