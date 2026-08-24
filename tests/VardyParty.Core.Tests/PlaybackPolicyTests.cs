using VardyParty.Playback;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests;

public class PlaybackPolicyTests
{
    [Theory]
    [InlineData(null, "http://a", false, true)]
    [InlineData(null, "http://a", true, false)]
    [InlineData("http://a", "http://a", false, false)]
    [InlineData("http://a", "HTTP://A", false, false)]
    [InlineData("http://a", "http://b", false, true)]
    [InlineData("http://a", "", false, false)]
    [InlineData("http://a", "  ", false, false)]
    public void CanAttach_MatchesLegacySwitchRules(string? current, string candidate, bool preparing, bool expected)
    {
        Assert.Equal(expected, PlaybackPolicy.CanAttach(current, candidate, preparing));
        Assert.Equal(expected, SwitchingDecision.CanSwitch(current, candidate ?? string.Empty, preparing));
    }

    [Fact]
    public void CanUserNavigate_RequiresIdleEnoughAndPool()
    {
        var snap = new PlaybackSessionSnapshot
        {
            State = PlaybackSessionState.Playing,
            IsPreparing = false
        };
        Assert.False(PlaybackPolicy.CanUserNavigate(snap, healthyStreamCount: 1));
        Assert.True(PlaybackPolicy.CanUserNavigate(snap, healthyStreamCount: 2));

        snap = new PlaybackSessionSnapshot { State = PlaybackSessionState.Playing, IsPreparing = true };
        Assert.False(PlaybackPolicy.CanUserNavigate(snap, 5));

        snap = new PlaybackSessionSnapshot { State = PlaybackSessionState.Closed, IsPreparing = false };
        Assert.False(PlaybackPolicy.CanUserNavigate(snap, 5));
    }

    [Fact]
    public void ShouldRetryFreshResolve_OnlyBeforeEstablishWithCache()
    {
        var snap = new PlaybackSessionSnapshot
        {
            UsedCachedUrl = true,
            CacheRetryUsed = false,
            HasEstablishedPlayback = false
        };
        Assert.True(PlaybackPolicy.ShouldRetryFreshResolve(snap));

        snap = new PlaybackSessionSnapshot
        {
            UsedCachedUrl = true,
            CacheRetryUsed = true,
            HasEstablishedPlayback = false
        };
        Assert.False(PlaybackPolicy.ShouldRetryFreshResolve(snap));

        snap = new PlaybackSessionSnapshot
        {
            UsedCachedUrl = true,
            CacheRetryUsed = false,
            HasEstablishedPlayback = true
        };
        Assert.False(PlaybackPolicy.ShouldRetryFreshResolve(snap));
    }

    [Fact]
    public void ShouldRevertAfterFailedSwitch_OnlyWhileSwitchingWithLastGood()
    {
        var snap = new PlaybackSessionSnapshot
        {
            HasEstablishedPlayback = true,
            State = PlaybackSessionState.Switching,
            LastGoodUrl = "http://good"
        };
        Assert.True(PlaybackPolicy.ShouldRevertAfterFailedSwitch(snap));

        snap = new PlaybackSessionSnapshot
        {
            HasEstablishedPlayback = true,
            State = PlaybackSessionState.Playing,
            LastGoodUrl = "http://good"
        };
        Assert.False(PlaybackPolicy.ShouldRevertAfterFailedSwitch(snap));
    }

    [Fact]
    public void IsHealthDeclined_UsesSharedWindowThresholds()
    {
        var window = new StreamMetricsWindow();
        Assert.False(PlaybackPolicy.IsHealthDeclined(window));

        window.AddBufferingEvent();
        window.AddBufferingEvent();
        window.AddBufferingEvent();
        Assert.False(PlaybackPolicy.IsHealthDeclined(window));
        window.AddBufferingEvent();
        Assert.True(PlaybackPolicy.IsHealthDeclined(window));
    }

    [Fact]
    public void ShouldAdvanceAfterEstablishedFailure_RequiresPlayingPool()
    {
        var playing = new PlaybackSessionSnapshot
        {
            HasEstablishedPlayback = true,
            State = PlaybackSessionState.Playing
        };
        Assert.True(PlaybackPolicy.ShouldAdvanceAfterEstablishedFailure(playing, 2));
        Assert.False(PlaybackPolicy.ShouldAdvanceAfterEstablishedFailure(playing, 1));

        var switching = new PlaybackSessionSnapshot
        {
            HasEstablishedPlayback = true,
            State = PlaybackSessionState.Switching
        };
        Assert.False(PlaybackPolicy.ShouldAdvanceAfterEstablishedFailure(switching, 3));
    }

    [Fact]
    public void ShouldAdvanceAfterFailedStart_WhenPoolRemainsAfterRemove()
    {
        var start = new PlaybackSessionSnapshot { HasEstablishedPlayback = false };
        Assert.True(PlaybackPolicy.ShouldAdvanceAfterFailedStart(start, healthyStreamCountAfterRemove: 1));
        Assert.False(PlaybackPolicy.ShouldAdvanceAfterFailedStart(start, healthyStreamCountAfterRemove: 0));

        var established = new PlaybackSessionSnapshot { HasEstablishedPlayback = true };
        Assert.False(PlaybackPolicy.ShouldAdvanceAfterFailedStart(established, 2));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void IsHardDownloadFailure_MatchesWindowsThreshold(int failures, bool expected)
        => Assert.Equal(expected, PlaybackPolicy.IsHardDownloadFailure(failures));

    [Fact]
    public void ShouldIgnoreClearedEngineError_LocksAndroidNullErrorNoOp()
    {
        Assert.True(PlaybackPolicy.ShouldIgnoreClearedEngineError(errorIsNull: true));
        Assert.False(PlaybackPolicy.ShouldIgnoreClearedEngineError(errorIsNull: false));
    }

    [Fact]
    public void IsCurrentGeneration_RejectsStaleEvents()
    {
        var snap = new PlaybackSessionSnapshot { AttachGeneration = 3 };
        Assert.True(PlaybackPolicy.IsCurrentGeneration(snap, 3));
        Assert.False(PlaybackPolicy.IsCurrentGeneration(snap, 2));
    }

    [Fact]
    public void CanUserNavigate_BlocksFailedState()
    {
        var snap = new PlaybackSessionSnapshot
        {
            State = PlaybackSessionState.Failed,
            IsPreparing = false
        };
        Assert.False(PlaybackPolicy.CanUserNavigate(snap, 5));
    }
}
