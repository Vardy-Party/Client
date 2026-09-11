using VardyParty.Catalog;
using VardyParty.Desktop.Services;
using Xunit;

namespace VardyParty.Desktop.Tests;

public class DesktopPlayerChromeTests
{
    [Fact]
    public void ApplyStreamChrome_FirstHealthyStream_ShowsToast()
    {
        // Arrange
        var sut = new DesktopPlayerChrome();

        // Act
        var action = sut.ApplyStreamChrome(1, 2, "720p", "FB", canRequestNext: true);

        // Assert
        Assert.Equal(DesktopPlayerChromeTimerAction.StartStreamToastHide, action);
        Assert.True(sut.StreamToastVisible);
        Assert.Equal("Stream: 1/2 (720p)", sut.StreamToastText);
        Assert.Equal("1/2", sut.StreamCountHint);
        Assert.True(sut.CanSwitchStream);
        Assert.True(sut.SourceBadgeIsFacebook);
        Assert.True(sut.NeedsExpandedChromeRow);
    }

    [Fact]
    public void ApplyStreamChrome_UnchangedWhileVisible_DoesNotRestartToast()
    {
        // Arrange
        var sut = new DesktopPlayerChrome();
        sut.ApplyStreamChrome(1, 2, "720p", "V2", canRequestNext: true);

        // Act
        var action = sut.ApplyStreamChrome(1, 2, "720p", "V2", canRequestNext: true);

        // Assert
        Assert.Equal(DesktopPlayerChromeTimerAction.None, action);
        Assert.True(sut.StreamToastVisible);
    }

    [Fact]
    public void ApplyStreamChrome_HiddenWithResolution_ReshowsLikeWindows()
    {
        // Arrange
        var sut = new DesktopPlayerChrome();
        sut.ApplyStreamChrome(1, 2, "720p", "V2", canRequestNext: true);
        sut.HideStreamToast();

        // Act
        var action = sut.ApplyStreamChrome(1, 2, "720p", "V2", canRequestNext: true);

        // Assert
        Assert.Equal(DesktopPlayerChromeTimerAction.StartStreamToastHide, action);
        Assert.True(sut.StreamToastVisible);
    }

    [Fact]
    public void ApplyStreamChrome_WhileInfoOpen_HidesToast()
    {
        // Arrange
        var sut = new DesktopPlayerChrome();
        sut.ShowInfo();

        // Act
        var action = sut.ApplyStreamChrome(2, 3, "1080p", null, canRequestNext: true);

        // Assert
        Assert.Equal(DesktopPlayerChromeTimerAction.CancelStreamToastHide, action);
        Assert.False(sut.StreamToastVisible);
        Assert.True(sut.InfoVisible);
        Assert.Equal(2, sut.StreamIndex);
        Assert.Equal(3, sut.StreamTotal);
    }

    [Fact]
    public void ApplyStreamChrome_SingleStream_CannotSwitch()
    {
        // Arrange
        var sut = new DesktopPlayerChrome();

        // Act
        sut.ApplyStreamChrome(1, 1, null, null, canRequestNext: true);

        // Assert
        Assert.False(sut.CanSwitchStream);
        Assert.Equal("Stream: 1/1", sut.StreamToastText);
    }

    [Fact]
    public void ToggleInfo_HidesMenuAndToast()
    {
        // Arrange
        var sut = new DesktopPlayerChrome();
        sut.ApplyStreamChrome(1, 2, null, null, canRequestNext: true);
        sut.ToggleMenu();

        // Act
        sut.ToggleInfo();

        // Assert
        Assert.True(sut.InfoVisible);
        Assert.False(sut.MenuVisible);
        Assert.False(sut.StreamToastVisible);
    }

    [Fact]
    public void ToggleTicker_StartsOnSameLeagueAndHidesMenu()
    {
        // Arrange
        var sut = new DesktopPlayerChrome();
        sut.ToggleMenu();

        // Act
        sut.ToggleTicker();

        // Assert
        Assert.True(sut.TickerVisible);
        Assert.Equal(ScoresTickerMode.SameLeagueInPlay, sut.TickerMode);
        Assert.False(sut.MenuVisible);
    }

    [Fact]
    public void CycleTickerMode_OnlyWhileVisible()
    {
        // Arrange
        var sut = new DesktopPlayerChrome();

        // Act
        var hidden = sut.CycleTickerMode();
        sut.ToggleTicker();
        var first = sut.CycleTickerMode();

        // Assert
        Assert.False(hidden);
        Assert.True(first);
        Assert.Equal(ScoresTickerMode.AllLeaguesInPlay, sut.TickerMode);
    }

    [Fact]
    public void OnEscape_DismissesMenuThenInfoThenClose()
    {
        // Arrange
        var sut = new DesktopPlayerChrome();
        sut.ToggleMenu();

        // Act
        var menu = sut.OnEscape();
        sut.ShowInfo();
        var info = sut.OnEscape();
        var close = sut.OnEscape();

        // Assert
        Assert.Equal(DesktopPlayerEscapeAction.DismissMenu, menu);
        Assert.Equal(DesktopPlayerEscapeAction.DismissInfo, info);
        Assert.Equal(DesktopPlayerEscapeAction.ClosePlayback, close);
        Assert.False(sut.MenuVisible);
        Assert.False(sut.InfoVisible);
    }

    [Fact]
    public void Reset_ClearsAllChrome()
    {
        // Arrange
        var sut = new DesktopPlayerChrome();
        sut.ApplyStreamChrome(2, 4, "1080p", "FB", canRequestNext: true);
        sut.ShowInfo();
        sut.ToggleTicker();
        sut.SetBuffering(true);

        // Act
        sut.Reset();

        // Assert
        Assert.False(sut.InfoVisible);
        Assert.False(sut.TickerVisible);
        Assert.False(sut.StreamToastVisible);
        Assert.False(sut.CanSwitchStream);
        Assert.False(sut.IsBuffering);
        Assert.Equal(0, sut.StreamTotal);
    }

    [Fact]
    public void StreamToastDuration_MatchesWindowsAndroid()
    {
        // Arrange
        // Act
        var duration = DesktopPlayerChrome.StreamToastDuration;

        // Assert
        Assert.Equal(10, duration.TotalSeconds);
    }
}
