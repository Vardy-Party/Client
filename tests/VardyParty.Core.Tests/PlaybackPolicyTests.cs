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
}
