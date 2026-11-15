using System;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Jon, 15 Nov 2026: a stream's height can change mid-play, so the stored height
/// must follow the settled measured size instead of pinning the old one.
/// </summary>
public class MeasuredPictureSizeTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StoredHeight720_SettledMeasured1080_ReplacesStoredHeight()
    {
        // Arrange
        var now = Start;
        var health = new StreamHealth { ProbedVideoWidth = 1280, ProbedVideoHeight = 720 };
        var settler = new DecodedPictureSizeSettler(() => now);
        settler.Observe(1280, 720);
        now += TimeSpan.FromSeconds(1);
        MeasuredPictureSize.Remember(health, settler.Tick());
        now += TimeSpan.FromSeconds(20);
        settler.Observe(1920, 1080);

        // Act
        var changedBeforeSettle = MeasuredPictureSize.Remember(health, settler.Published);
        var heightBeforeSettle = health.ProbedVideoHeight;
        now += TimeSpan.FromSeconds(1);
        var changedAfterSettle = MeasuredPictureSize.Remember(health, settler.Tick());

        // Assert
        Assert.False(changedBeforeSettle);
        Assert.Equal(720, heightBeforeSettle);
        Assert.True(changedAfterSettle);
        Assert.Equal(1920, health.ProbedVideoWidth);
        Assert.Equal(1080, health.ProbedVideoHeight);
        Assert.Equal("1920x1080", health.GetProbedResolution());
    }

    [Fact]
    public void BriefBlip_DoesNotChangeStoredHeight()
    {
        // Arrange
        var now = Start;
        var health = new StreamHealth { ProbedVideoWidth = 1280, ProbedVideoHeight = 720 };
        var settler = new DecodedPictureSizeSettler(() => now);
        settler.Observe(1280, 720);
        now += TimeSpan.FromSeconds(1);
        settler.Tick();

        // Act
        settler.Observe(1920, 1080);
        now += TimeSpan.FromMilliseconds(200);
        var changed = MeasuredPictureSize.Remember(health, settler.Observe(1280, 720));

        // Assert
        Assert.False(changed);
        Assert.Equal(720, health.ProbedVideoHeight);
    }

    [Fact]
    public void Remember_NoHealthOrNoSettledSize_ChangesNothing()
    {
        // Arrange
        var health = new StreamHealth { ProbedVideoWidth = 1280, ProbedVideoHeight = 720 };

        // Act
        var noHealth = MeasuredPictureSize.Remember(null, (1920, 1080));
        var noSize = MeasuredPictureSize.Remember(health, null);

        // Assert
        Assert.False(noHealth);
        Assert.False(noSize);
        Assert.Equal(720, health.ProbedVideoHeight);
    }
}
