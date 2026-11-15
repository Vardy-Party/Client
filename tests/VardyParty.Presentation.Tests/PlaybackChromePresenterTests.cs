using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoFixture;
using VardyParty.Catalog;
using VardyParty.Kernel;
using VardyParty.Presentation;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class PlaybackChromePresenterTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void TryDismissLayer_Order_IsMenuThenInfoThenScores()
    {
        // Arrange
        var sut = new PlaybackChromePresenter();
        sut.ToggleScores();
        sut.ShowVideoInfo();
        sut.ToggleMenu();
        Assert.True(sut.IsMenuVisible);
        Assert.True(sut.IsVideoInfoVisible);
        Assert.True(sut.IsScoresVisible);

        // Act
        var dismissedMenu = sut.TryDismissLayer();
        // Assert
        Assert.True(dismissedMenu);
        Assert.False(sut.IsMenuVisible);

        // Act
        var dismissedInfo = sut.TryDismissLayer();
        // Assert
        Assert.True(dismissedInfo);
        Assert.False(sut.IsVideoInfoVisible);

        // Act
        var dismissedScores = sut.TryDismissLayer();
        // Assert
        Assert.True(dismissedScores);
        Assert.False(sut.IsScoresVisible);

        // Act
        var dismissedNothing = sut.TryDismissLayer();
        // Assert
        Assert.False(dismissedNothing);
    }

    [Fact]
    public void ShowVideoInfo_DismissesToastAndLocksOverlay()
    {
        // Arrange
        var sut = new PlaybackChromePresenter();
        var info = _fixture.Build<PlayerOverlayInfo>()
            .With(i => i.Index, 1)
            .With(i => i.Total, 3)
            .With(i => i.Resolution, "1280x720")
            .Create();
        sut.ApplyOverlayInfo(info);
        Assert.True(sut.IsStreamToastVisible);

        // Act
        sut.ShowVideoInfo();

        // Assert
        Assert.True(sut.IsVideoInfoVisible);
        Assert.True(sut.OverlayLocked);
        Assert.False(sut.IsStreamToastVisible);
        Assert.False(sut.IsMenuVisible);
    }

    [Fact]
    public void ApplyOverlayInfo_DoesNotShowToastWhileVideoInfoOpen()
    {
        // Arrange
        var sut = new PlaybackChromePresenter();
        sut.ShowVideoInfo();
        var toastRequested = false;
        sut.StreamToastRequested += (_, __) => toastRequested = true;
        var info = _fixture.Build<PlayerOverlayInfo>()
            .With(i => i.Index, 2)
            .With(i => i.Total, 4)
            .With(i => i.Resolution, "1920x1080")
            .With(i => i.Channel, "Channel West")
            .With(i => i.SourceLabel, "V1")
            .Create();

        // Act
        sut.ApplyOverlayInfo(info);

        // Assert
        Assert.False(toastRequested);
        Assert.False(sut.IsStreamToastVisible);
        Assert.Equal("1080p", sut.StreamToast!.VerticalResolutionLabel);
        Assert.Equal("2/4 · V1 · Channel West · 1080p", sut.StreamToast.Text);
    }

    [Fact]
    public void ApplyOverlayInfo_UpdatesToastTextWhenResolutionBecomesKnown()
    {
        // Arrange
        var sut = new PlaybackChromePresenter();
        var requests = new List<string>();
        sut.StreamToastRequested += (_, toast) => requests.Add(toast.Text);
        var opening = _fixture.Build<PlayerOverlayInfo>()
            .With(i => i.Index, 1)
            .With(i => i.Total, 2)
            .With(i => i.Channel, "Channel West")
            .With(i => i.SourceLabel, "V1")
            .With(i => i.Resolution, (string?)null)
            .Create();
        sut.ApplyOverlayInfo(opening);
        Assert.Equal("1/2 · V1 · Channel West", sut.StreamToast!.Text);

        // Act — picture size arrives; index/total stay the same
        var measured = _fixture.Build<PlayerOverlayInfo>()
            .With(i => i.Index, 1)
            .With(i => i.Total, 2)
            .With(i => i.Channel, "Channel West")
            .With(i => i.SourceLabel, "V1")
            .With(i => i.Resolution, "1280x720")
            .Create();
        sut.ApplyOverlayInfo(measured);

        // Assert
        Assert.Equal("1/2 · V1 · Channel West · 720p", sut.StreamToast!.Text);
        Assert.Equal("720p", sut.StreamToast.VerticalResolutionLabel);
        Assert.True(sut.IsStreamToastVisible);
        // Height-only updates must not re-request the toast (would restart auto-hide).
        Assert.Equal("1/2 · V1 · Channel West", Assert.Single(requests));
    }

    [Fact]
    public void ToggleScores_ResetsModeOnShow()
    {
        // Arrange
        var sut = new PlaybackChromePresenter();
        sut.ToggleScores();
        sut.CycleScoresMode();
        Assert.Equal(ScoresTickerMode.AllLeaguesInPlay, sut.ScoresMode);

        // Act
        sut.ToggleScores(); // hide
        sut.ToggleScores(); // show again

        // Assert
        Assert.True(sut.IsScoresVisible);
        Assert.Equal(ScoresTickerMode.SameLeagueInPlay, sut.ScoresMode);
    }

    [Fact]
    public void CycleScoresMode_NoOpWhenHidden()
    {
        // Arrange
        var sut = new PlaybackChromePresenter();

        // Act
        sut.CycleScoresMode();

        // Assert
        Assert.Equal(ScoresTickerMode.SameLeagueInPlay, sut.ScoresMode);
    }

    [Fact]
    public void NotifyHealthyCount_SetsCanGoNext()
    {
        // Arrange
        var sut = new PlaybackChromePresenter();

        // Act
        sut.NotifyHealthyCount(1);
        var alone = sut.CanGoNext;
        sut.NotifyHealthyCount(3);
        var multi = sut.CanGoNext;

        // Assert
        Assert.False(alone);
        Assert.True(multi);
    }

    [Fact]
    public async Task ReportBadStreamAsync_UnavailableWhenNoCallback()
    {
        // Arrange
        var sut = new PlaybackChromePresenter();

        // Act
        await sut.ReportBadStreamAsync();

        // Assert
        Assert.Equal(PlaybackReportUiState.Unavailable, sut.ReportState);
        Assert.Equal("Report unavailable", sut.ReportStatusText);
    }

    [Fact]
    public async Task ReportBadStreamAsync_SucceedsViaCallback()
    {
        // Arrange
        string? reason = null;
        var sut = new PlaybackChromePresenter(
            reportBadStream: (r, _) =>
            {
                reason = r;
                return Task.CompletedTask;
            });

        // Act
        await sut.ReportBadStreamAsync();

        // Assert
        Assert.Equal("User reported bad stream", reason);
        Assert.Equal(PlaybackReportUiState.Succeeded, sut.ReportState);
        Assert.Equal("Stream reported", sut.ReportStatusText);
    }

    [Fact]
    public async Task ReportBadStreamAsync_FailedOnException()
    {
        // Arrange
        var sut = new PlaybackChromePresenter(
            reportBadStream: (_, _) => throw new InvalidOperationException("boom"));

        // Act
        await sut.ReportBadStreamAsync();

        // Assert
        Assert.Equal(PlaybackReportUiState.Failed, sut.ReportState);
        Assert.Equal("Report failed", sut.ReportStatusText);
    }

    [Fact]
    public void Exit_CleansUpAndRaisesExitRequested()
    {
        // Arrange
        var cleaned = false;
        var exited = false;
        var sut = new PlaybackChromePresenter(cleanupPool: () => cleaned = true);
        sut.ExitRequested += (_, __) => exited = true;
        sut.ToggleMenu();
        sut.ShowVideoInfo();

        // Act
        sut.Exit();

        // Assert
        Assert.True(cleaned);
        Assert.True(exited);
        Assert.False(sut.IsMenuVisible);
        Assert.False(sut.IsVideoInfoVisible);
    }

    [Fact]
    public async Task RequestNextStreamAsync_UsesCallbackWhenCanGoNext()
    {
        // Arrange
        var called = false;
        var sut = new PlaybackChromePresenter(requestNext: () =>
        {
            called = true;
            return Task.CompletedTask;
        });
        sut.NotifyHealthyCount(2);

        // Act
        await sut.RequestNextStreamAsync();

        // Assert
        Assert.True(called);
    }

    [Fact]
    public async Task RequestNextStreamAsync_NoOpWhenCannotGoNext()
    {
        // Arrange
        var called = false;
        var sut = new PlaybackChromePresenter(requestNext: () =>
        {
            called = true;
            return Task.CompletedTask;
        });
        sut.NotifyHealthyCount(1);

        // Act
        await sut.RequestNextStreamAsync();

        // Assert
        Assert.False(called);
    }

    [Theory]
    [InlineData(PlaybackChromeContentState.Opening)]
    [InlineData(PlaybackChromeContentState.Recovering)]
    [InlineData(PlaybackChromeContentState.Switching)]
    [InlineData(PlaybackChromeContentState.Error)]
    public void SetContentState_Awaiting_ForcesNextAndHoldsToast(PlaybackChromeContentState state)
    {
        // Arrange
        var sut = new PlaybackChromePresenter();
        var info = _fixture.Build<PlayerOverlayInfo>()
            .With(i => i.Index, 2)
            .With(i => i.Total, 5)
            .Create();
        sut.ApplyOverlayInfo(info);
        sut.DismissStreamToast();
        Assert.False(sut.IsStreamToastVisible);

        // Act
        sut.SetContentState(state);

        // Assert
        Assert.True(sut.IsAwaitingPlayableContent);
        Assert.True(sut.IsNextForcedVisible);
        Assert.True(sut.ShouldHoldStreamToast);
        Assert.True(sut.IsStreamToastVisible);
        sut.DismissStreamToast();
        Assert.True(sut.IsStreamToastVisible);
    }

    [Fact]
    public void SetContentState_Playing_AllowsHoverFadeAndToastDismiss()
    {
        // Arrange
        var sut = new PlaybackChromePresenter();
        var info = _fixture.Build<PlayerOverlayInfo>()
            .With(i => i.Index, 1)
            .With(i => i.Total, 3)
            .Create();
        sut.ApplyOverlayInfo(info);
        sut.SetContentState(PlaybackChromeContentState.Switching);
        Assert.True(sut.IsNextForcedVisible);

        // Act
        sut.SetContentState(PlaybackChromeContentState.Playing);

        // Assert
        Assert.False(sut.IsAwaitingPlayableContent);
        Assert.False(sut.IsNextForcedVisible);
        Assert.True(sut.CanGoNext);
        Assert.False(sut.ShouldHoldStreamToast);
        sut.DismissStreamToast();
        Assert.False(sut.IsStreamToastVisible);
    }

    [Fact]
    public void IsNextForcedVisible_RequiresCanGoNext()
    {
        // Arrange
        var sut = new PlaybackChromePresenter();
        sut.NotifyHealthyCount(1);

        // Act
        sut.SetContentState(PlaybackChromeContentState.Opening);

        // Assert
        Assert.True(sut.IsAwaitingPlayableContent);
        Assert.False(sut.IsNextForcedVisible);
    }

    [Fact]
    public void ApplyOverlayInfo_WhileSwitching_KeepsToastVisible()
    {
        // Arrange
        var sut = new PlaybackChromePresenter();
        sut.SetContentState(PlaybackChromeContentState.Switching);
        var first = _fixture.Build<PlayerOverlayInfo>()
            .With(i => i.Index, 1)
            .With(i => i.Total, 4)
            .Create();
        sut.ApplyOverlayInfo(first);
        Assert.True(sut.IsStreamToastVisible);

        // Act — same index (no change) must still hold toast while switching
        sut.ApplyOverlayInfo(first);

        // Assert
        Assert.True(sut.ShouldHoldStreamToast);
        Assert.True(sut.IsStreamToastVisible);
        Assert.True(sut.IsNextForcedVisible);
    }

    [Fact]
    public void ShowVideoInfo_WhileSwitching_StillDismissesToast()
    {
        // Arrange
        var sut = new PlaybackChromePresenter();
        var info = _fixture.Build<PlayerOverlayInfo>()
            .With(i => i.Index, 3)
            .With(i => i.Total, 6)
            .Create();
        sut.ApplyOverlayInfo(info);
        sut.SetContentState(PlaybackChromeContentState.Switching);
        Assert.True(sut.IsStreamToastVisible);

        // Act
        sut.ShowVideoInfo();

        // Assert
        Assert.True(sut.IsVideoInfoVisible);
        Assert.False(sut.IsStreamToastVisible);
        Assert.False(sut.ShouldHoldStreamToast);
        Assert.True(sut.IsNextForcedVisible);
    }

    [Theory]
    [InlineData(PlaybackChromeContentState.Opening)]
    [InlineData(PlaybackChromeContentState.Playing)]
    public void ResolveFromPlayback_Error_SticksAcrossOpeningAndNonEstablishedPlaying(
        PlaybackChromeContentState proposed)
    {
        // Arrange
        var previous = PlaybackChromeContentState.Error;

        // Act
        var next = PlaybackChromePresenter.ResolveFromPlayback(
            previous,
            proposed,
            hasEstablishedPlayback: false);

        // Assert
        Assert.Equal(PlaybackChromeContentState.Error, next);
    }

    [Fact]
    public void ResolveFromPlayback_Error_ClearsOnEstablishedPlaying()
    {
        // Arrange / Act
        var next = PlaybackChromePresenter.ResolveFromPlayback(
            PlaybackChromeContentState.Error,
            PlaybackChromeContentState.Playing,
            hasEstablishedPlayback: true);

        // Assert
        Assert.Equal(PlaybackChromeContentState.Playing, next);
    }

    [Theory]
    [InlineData(PlaybackChromeContentState.Switching)]
    [InlineData(PlaybackChromeContentState.Recovering)]
    public void ResolveFromPlayback_Error_ClearsOnDeliberateRecovery(
        PlaybackChromeContentState proposed)
    {
        // Arrange / Act
        var next = PlaybackChromePresenter.ResolveFromPlayback(
            PlaybackChromeContentState.Error,
            proposed,
            hasEstablishedPlayback: false);

        // Assert
        Assert.Equal(proposed, next);
    }

    [Fact]
    public void ApplyOverlayInfo_WhileSwitching_OmitsPreviousMeasuredHeight()
    {
        var sut = new PlaybackChromePresenter();
        sut.ApplyOverlayInfo(new PlayerOverlayInfo
        {
            Index = 1,
            Total = 2,
            Channel = "Channel West",
            SourceLabel = "V1",
            Resolution = "1280x720"
        });
        Assert.Equal("1/2 · V1 · Channel West · 720p", sut.StreamToast!.Text);

        sut.SetContentState(PlaybackChromeContentState.Switching);
        sut.ApplyOverlayInfo(new PlayerOverlayInfo
        {
            Index = 2,
            Total = 2,
            Channel = "Channel North",
            SourceLabel = "V1",
            Resolution = "1280x720"
        }, whileSwitching: true);

        Assert.Equal("2/2 · V1 · Channel North", sut.StreamToast!.Text);
        Assert.Null(sut.StreamToast.VerticalResolutionLabel);
        Assert.True(sut.IsStreamToastVisible);
    }

    [Fact]
    public void ApplyOverlayInfo_WhileSwitching_ShowsTargetBitstreamProbe()
    {
        var sut = new PlaybackChromePresenter();
        sut.SetContentState(PlaybackChromeContentState.Switching);
        sut.ApplyOverlayInfo(new PlayerOverlayInfo
        {
            Index = 2,
            Total = 2,
            Channel = "Channel North",
            SourceLabel = "V1",
            Resolution = "960x540",
            ProbedResolution = "1920x1080"
        }, whileSwitching: true);

        Assert.Equal("2/2 · V1 · Channel North · 1080p", sut.StreamToast!.Text);
        Assert.Equal("1080p", sut.StreamToast.VerticalResolutionLabel);
        Assert.DoesNotContain("540p", sut.StreamToast.Text);
    }
}
