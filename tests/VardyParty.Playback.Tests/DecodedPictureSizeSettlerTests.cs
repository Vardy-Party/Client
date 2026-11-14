using System;
using Xunit;
using VardyParty.Playback;

namespace VardyParty.Playback.Tests;

public class DecodedPictureSizeSettlerTests
{
    [Fact]
    public void FirstSample_IsNotPublishedUntilItHasStayedForSettle()
    {
        var now = new DateTimeOffset(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);
        var sut = new DecodedPictureSizeSettler(() => now);

        Assert.Null(sut.Observe(1920, 1080));
        Assert.False(sut.HasPublished);

        now += TimeSpan.FromMilliseconds(200);
        Assert.Null(sut.Tick());

        now += TimeSpan.FromMilliseconds(800);
        var published = sut.Tick();

        Assert.Equal((1920, 1080), published);
        Assert.True(sut.HasPublished);
    }

    [Fact]
    public void Flash1080ThenRealSize_NeverPublishesTheFlash()
    {
        var now = new DateTimeOffset(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);
        var sut = new DecodedPictureSizeSettler(() => now);

        Assert.Null(sut.Observe(1920, 1080));

        now += TimeSpan.FromMilliseconds(150);
        Assert.Null(sut.Observe(854, 480));
        Assert.False(sut.HasPublished);

        now += TimeSpan.FromMilliseconds(800);
        Assert.Null(sut.Tick());

        now += TimeSpan.FromMilliseconds(200);
        var published = sut.Tick();

        Assert.Equal((854, 480), published);
        Assert.NotEqual(1080, published!.Value.Height);
    }

    [Fact]
    public void Observe_IgnoresZeroDimensions()
    {
        var now = new DateTimeOffset(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);
        var sut = new DecodedPictureSizeSettler(() => now);

        Assert.Null(sut.Observe(0, 0));
        Assert.Null(sut.Observe(1920, 0));
        Assert.False(sut.HasPublished);

        now += TimeSpan.FromSeconds(2);
        Assert.Null(sut.Tick());
    }

    [Fact]
    public void Reset_ClearsPublishedAndPendingSize()
    {
        var now = new DateTimeOffset(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);
        var sut = new DecodedPictureSizeSettler(() => now);
        sut.Observe(1280, 720);
        now += TimeSpan.FromSeconds(1);
        Assert.Equal((1280, 720), sut.Tick());

        sut.Reset();

        Assert.Null(sut.Published);
        Assert.False(sut.HasPublished);
        Assert.Null(sut.Observe(1920, 1080));
    }

    [Fact]
    public void PublishedSizeThatThenChanges_IsWithdrawnUntilTheNewSizeSettles()
    {
        var now = new DateTimeOffset(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);
        var sut = new DecodedPictureSizeSettler(() => now);
        sut.Observe(1280, 720);
        now += TimeSpan.FromSeconds(1);
        Assert.Equal((1280, 720), sut.Tick());

        now += TimeSpan.FromMilliseconds(50);
        Assert.Null(sut.Observe(1920, 1080));
        Assert.False(sut.HasPublished);

        now += TimeSpan.FromSeconds(1);
        Assert.Equal((1920, 1080), sut.Tick());
    }
}
