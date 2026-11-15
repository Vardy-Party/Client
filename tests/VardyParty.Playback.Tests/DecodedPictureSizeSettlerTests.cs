using System;
using Xunit;
using VardyParty.Playback;

namespace VardyParty.Playback.Tests;

public class DecodedPictureSizeSettlerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstSample_IsNotPublishedUntilItHasStayedForSettle()
    {
        // Arrange
        var now = Start;
        var sut = new DecodedPictureSizeSettler(() => now);

        // Act
        var onObserve = sut.Observe(1920, 1080);
        var publishedOnObserve = sut.HasPublished;
        now += TimeSpan.FromMilliseconds(200);
        var early = sut.Tick();
        now += TimeSpan.FromMilliseconds(800);
        var published = sut.Tick();

        // Assert
        Assert.Null(onObserve);
        Assert.False(publishedOnObserve);
        Assert.Null(early);
        Assert.Equal((1920, 1080), published);
        Assert.True(sut.HasPublished);
    }

    [Fact]
    public void Flash1080ThenRealSize_NeverPublishesTheFlash()
    {
        // Arrange
        var now = Start;
        var sut = new DecodedPictureSizeSettler(() => now);

        // Act
        var flash = sut.Observe(1920, 1080);
        now += TimeSpan.FromMilliseconds(150);
        var real = sut.Observe(854, 480);
        var publishedAfterReal = sut.HasPublished;
        now += TimeSpan.FromMilliseconds(800);
        var early = sut.Tick();
        now += TimeSpan.FromMilliseconds(200);
        var published = sut.Tick();

        // Assert
        Assert.Null(flash);
        Assert.Null(real);
        Assert.False(publishedAfterReal);
        Assert.Null(early);
        Assert.Equal((854, 480), published);
        Assert.NotEqual(1080, published!.Value.Height);
    }

    [Fact]
    public void Observe_IgnoresZeroDimensions()
    {
        // Arrange
        var now = Start;
        var sut = new DecodedPictureSizeSettler(() => now);

        // Act
        var zero = sut.Observe(0, 0);
        var zeroHeight = sut.Observe(1920, 0);
        var publishedAfterObserve = sut.HasPublished;
        now += TimeSpan.FromSeconds(2);
        var tick = sut.Tick();

        // Assert
        Assert.Null(zero);
        Assert.Null(zeroHeight);
        Assert.False(publishedAfterObserve);
        Assert.Null(tick);
    }

    [Fact]
    public void Reset_ClearsPublishedAndPendingSize()
    {
        // Arrange
        var now = Start;
        var sut = new DecodedPictureSizeSettler(() => now);
        sut.Observe(1280, 720);
        now += TimeSpan.FromSeconds(1);
        var publishedBeforeReset = sut.Tick();

        // Act
        sut.Reset();
        var afterReset = sut.Observe(1920, 1080);

        // Assert
        Assert.Equal((1280, 720), publishedBeforeReset);
        Assert.Null(sut.Published);
        Assert.False(sut.HasPublished);
        Assert.Null(afterReset);
    }

    [Fact]
    public void MidPlay720ThenSteady1080_KeepsShowing720UntilThe1080Settles()
    {
        // Arrange
        var now = Start;
        var sut = new DecodedPictureSizeSettler(() => now);
        sut.Observe(1280, 720);
        now += TimeSpan.FromSeconds(1);
        var first = sut.Tick();

        // Act
        now += TimeSpan.FromSeconds(30);
        var onChange = sut.Observe(1920, 1080);
        var needsTick = sut.NeedsTick;
        now += TimeSpan.FromMilliseconds(500);
        var halfway = sut.Tick();
        now += TimeSpan.FromMilliseconds(500);
        var settled = sut.Tick();

        // Assert
        Assert.Equal((1280, 720), first);
        Assert.Equal((1280, 720), onChange);
        Assert.True(needsTick);
        Assert.Equal((1280, 720), halfway);
        Assert.Equal((1920, 1080), settled);
        Assert.False(sut.NeedsTick);
    }

    [Fact]
    public void MidPlayBriefBlip_NeverChangesThePublishedSize()
    {
        // Arrange
        var now = Start;
        var sut = new DecodedPictureSizeSettler(() => now);
        sut.Observe(1280, 720);
        now += TimeSpan.FromSeconds(1);
        sut.Tick();

        // Act
        now += TimeSpan.FromSeconds(10);
        var duringBlip = sut.Observe(1920, 1080);
        now += TimeSpan.FromMilliseconds(300);
        var back = sut.Observe(1280, 720);
        now += TimeSpan.FromSeconds(2);
        var later = sut.Tick();

        // Assert
        Assert.Equal((1280, 720), duringBlip);
        Assert.Equal((1280, 720), back);
        Assert.Equal((1280, 720), later);
        Assert.False(sut.NeedsTick);
    }
}
