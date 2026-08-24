using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests;

public class StreamMetricsWindowTests
{
    [Fact]
    public void Declines_AfterFourBufferingEvents()
    {
        var w = new StreamMetricsWindow();
        for (var i = 0; i < 3; i++)
        {
            w.AddBufferingEvent();
            Assert.False(w.IsHealthDeclined());
        }

        w.AddBufferingEvent();
        Assert.True(w.IsHealthDeclined());
    }

    [Fact]
    public void Declines_WhenAverageBitrateBelow300()
    {
        var w = new StreamMetricsWindow();
        w.AddBitrate(100);
        w.AddBitrate(200);
        w.AddBitrate(250);
        Assert.True(w.IsHealthDeclined());
    }

    [Fact]
    public void Declines_AfterThreeErrors()
    {
        var w = new StreamMetricsWindow();
        w.AddError();
        w.AddError();
        Assert.False(w.IsHealthDeclined());
        w.AddError();
        Assert.True(w.IsHealthDeclined());
    }

    [Fact]
    public void DoesNotDecline_WithFewerThanThreeBitrateSamples()
    {
        var w = new StreamMetricsWindow();
        w.AddBitrate(50);
        w.AddBitrate(50);
        Assert.False(w.IsHealthDeclined());
    }

    [Fact]
    public void Declines_WhenTenSamplesAndLastThreeAllBelow500()
    {
        var w = new StreamMetricsWindow();
        for (var i = 0; i < 7; i++)
            w.AddBitrate(2000);
        w.AddBitrate(400);
        w.AddBitrate(400);
        w.AddBitrate(400);
        Assert.True(w.IsHealthDeclined());
    }

    [Fact]
    public void DoesNotDecline_WhenLastThreeAreHealthyBitrate()
    {
        var w = new StreamMetricsWindow();
        w.AddBitrate(2000);
        w.AddBitrate(1800);
        w.AddBitrate(1600);
        Assert.False(w.IsHealthDeclined());
    }
}
