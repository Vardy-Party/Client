using System;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class ScreenAwakePolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Hold_WhileFindingStreamsOrPlaying(bool finding, bool playing, bool expected)
    {
        Assert.Equal(expected, ScreenAwakePolicy.Hold(finding, playing));
    }

    /// <summary>
    /// Live run: the player opened, playback visibility fired on a pool thread,
    /// the keep-screen-on flag was set from that thread, and Android aborted the
    /// process. The flag is set only inside the UI dispatch.
    /// </summary>
    [Fact]
    public void Apply_SetsTheWindowFlagOnlyThroughTheUiDispatcher()
    {
        var setOnCallingThread = false;
        bool? applied = null;
        Action? queued = null;

        ScreenAwakePolicy.Apply(
            on: true,
            dispatchToUi: work => queued = work,
            setWindowFlag: on =>
            {
                setOnCallingThread = queued is null;
                applied = on;
            });

        Assert.False(setOnCallingThread);
        Assert.Null(applied);
        Assert.NotNull(queued);

        queued!();

        Assert.True(applied);
    }

    [Fact]
    public void Apply_PassesTheReleaseThroughTheSameDispatcher()
    {
        bool? applied = null;

        ScreenAwakePolicy.Apply(on: false, dispatchToUi: work => work(), setWindowFlag: on => applied = on);

        Assert.False(applied);
    }
}
