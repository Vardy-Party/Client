using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

public class PlaybackWaitChromeTests
{
    [Fact]
    public void Shows_WhilePreparing_EvenIfNotMarkedLoading()
    {
        Assert.True(PlaybackWaitChrome.ShouldShowWaitIndicator(
            isReady: false, isLoading: false, isPreparing: true));
    }

    [Fact]
    public void Shows_WhileLoading_AfterReadyStartedRebuffering()
    {
        Assert.True(PlaybackWaitChrome.ShouldShowWaitIndicator(
            isReady: true, isLoading: true, isPreparing: false));
    }

    [Fact]
    public void Shows_WhenNotReady_WithoutLoadingCallback()
    {
        Assert.True(PlaybackWaitChrome.ShouldShowWaitIndicator(
            isReady: false, isLoading: false, isPreparing: false));
    }

    [Fact]
    public void Hides_WhenReadyAndIdle()
    {
        Assert.False(PlaybackWaitChrome.ShouldShowWaitIndicator(
            isReady: true, isLoading: false, isPreparing: false));
    }

    [Fact]
    public void Hides_WhenEnded()
    {
        Assert.False(PlaybackWaitChrome.ShouldShowWaitIndicator(
            isReady: false, isLoading: false, isPreparing: false, isEnded: true));
    }
}
