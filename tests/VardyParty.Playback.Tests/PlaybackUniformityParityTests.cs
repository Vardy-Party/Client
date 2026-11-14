using System;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Phase 3 / 4 parity: one live-buffer target, one height-settle rule,
/// one no-picture timeout, and one health-metrics period shared by
/// Android, Windows, and Linux.
/// </summary>
public class PlaybackUniformityParityTests
{
    [Fact]
    public void LiveBufferTarget_Is25SecondsOnEveryPlatform()
    {
        Assert.Equal(25, PlaybackPolicy.DesiredLiveOffsetSeconds);
    }

    [Fact]
    public void LibVlcNetworkCaching_IsNotThe25SecondLiveOffset()
    {
        // LibVLC --network-caching is a pre-roll cache, not a live-edge
        // offset. Mapping DesiredLiveOffsetSeconds (25s) onto it would delay
        // the first picture by about 25s.
        Assert.Equal(4_000, PlaybackPolicy.LibVlcNetworkCachingMs);
        Assert.Equal(12_000, PlaybackPolicy.LibVlcTestHostNetworkCachingMs);
        Assert.NotEqual(
            PlaybackPolicy.DesiredLiveOffsetSeconds * 1000,
            PlaybackPolicy.LibVlcNetworkCachingMs);
        Assert.Equal(
            PlaybackPolicy.LibVlcNetworkCachingMs,
            PlaybackPolicy.ResolveLibVlcNetworkCachingMs(conservativeTestHost: false));
        Assert.Equal(
            PlaybackPolicy.LibVlcTestHostNetworkCachingMs,
            PlaybackPolicy.ResolveLibVlcNetworkCachingMs(conservativeTestHost: true));
    }

    [Fact]
    public void DecodedPictureSettle_IsOneSecondAndUsedByTheSharedSettler()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), PlaybackPolicy.DecodedPictureSettle);
        Assert.Equal(PlaybackPolicy.DecodedPictureSettle, DecodedPictureSizeSettler.Settle);
    }

    [Fact]
    public void NoVideoTimeout_IsEightSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), PlaybackPolicy.NoVideoTimeout);
        Assert.True(PlaybackPolicy.HasNoVideoTimedOut(
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch + PlaybackPolicy.NoVideoTimeout,
            PlaybackPolicy.NoVideoTimeout));
        Assert.False(PlaybackPolicy.HasNoVideoTimedOut(
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch + PlaybackPolicy.NoVideoTimeout - TimeSpan.FromMilliseconds(1),
            PlaybackPolicy.NoVideoTimeout));
    }

    [Fact]
    public void HealthMetricsInterval_IsThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), PlaybackPolicy.HealthMetricsInterval);
    }
}