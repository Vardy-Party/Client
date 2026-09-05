using System;
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

        // Act / Assert
        Assert.True(sut.IsMenuVisible);
        Assert.True(sut.IsVideoInfoVisible);
        Assert.True(sut.IsScoresVisible);

        Assert.True(sut.TryDismissLayer());
        Assert.False(sut.IsMenuVisible);

        Assert.True(sut.TryDismissLayer());
        Assert.False(sut.IsVideoInfoVisible);

        Assert.True(sut.TryDismissLayer());
        Assert.False(sut.IsScoresVisible);

        Assert.False(sut.TryDismissLayer());
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
            .Create();

        // Act
        sut.ApplyOverlayInfo(info);

        // Assert
        Assert.False(toastRequested);
        Assert.False(sut.IsStreamToastVisible);
        Assert.Equal("Stream: 2/4 (1080p)", sut.StreamToast!.Text);
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
}
