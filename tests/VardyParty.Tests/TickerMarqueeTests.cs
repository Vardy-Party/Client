using System;
using AutoFixture;
using Xunit;

namespace VardyParty.Tests;

public class TickerMarqueeTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void ShouldLoop_WhenContentWiderThanViewport_ReturnsTrue()
    {
        // Arrange
        var contentWidth = Math.Abs(_fixture.Create<double>()) + 200;
        var viewportWidth = contentWidth - 40;

        // Act
        var shouldLoop = TickerMarquee.ShouldLoop(contentWidth, viewportWidth);

        // Assert
        Assert.True(shouldLoop);
    }

    [Fact]
    public void ShouldLoop_WhenContentFits_ReturnsFalse()
    {
        // Arrange
        var viewportWidth = Math.Abs(_fixture.Create<double>()) + 200;
        var contentWidth = viewportWidth - 40;

        // Act
        var shouldLoop = TickerMarquee.ShouldLoop(contentWidth, viewportWidth);

        // Assert
        Assert.False(shouldLoop);
    }

    [Fact]
    public void LoopPeriod_AddsGapToContentWidth()
    {
        // Arrange
        var contentWidth = Math.Abs(_fixture.Create<double>()) + 10;
        var gapWidth = Math.Abs(_fixture.Create<double>()) + 1;

        // Act
        var period = TickerMarquee.LoopPeriod(contentWidth, gapWidth);

        // Assert
        Assert.Equal(contentWidth + gapWidth, period, 5);
    }

    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(-100, 100, 0)]
    [InlineData(-101.5, 100, -1.5)]
    [InlineData(10, 100, -90)]
    [InlineData(-250, 100, -50)]
    public void Wrap_MapsOffsetIntoOneLoop(double offset, double loopPeriod, double expected)
    {
        // Arrange
        // Act
        var wrapped = TickerMarquee.Wrap(offset, loopPeriod);

        // Assert
        Assert.Equal(expected, wrapped, 5);
    }

    [Fact]
    public void AdvanceLeft_IsSeamlessAcrossLoopBoundary()
    {
        // Arrange
        var loopPeriod = 80.0;
        var pixels = 1.5;
        var offset = -(loopPeriod - 0.5);

        // Act
        var next = TickerMarquee.AdvanceLeft(offset, pixels, loopPeriod);

        // Assert
        Assert.Equal(-1.0, next, 5);
    }

    [Fact]
    public void WrapPositive_KeepsDistanceInsideLoop()
    {
        // Arrange
        var loopPeriod = Math.Abs(_fixture.Create<int>() % 80) + 20d;
        var distance = loopPeriod * 3 + 7;

        // Act
        var wrapped = TickerMarquee.WrapPositive(distance, loopPeriod);

        // Assert
        Assert.Equal(7, wrapped, 5);
    }

    [Fact]
    public void Wrap_ZeroPeriod_ReturnsZero()
    {
        // Arrange
        var offset = _fixture.Create<double>();

        // Act
        var wrapped = TickerMarquee.Wrap(offset, 0);

        // Assert
        Assert.Equal(0, wrapped);
    }
}
