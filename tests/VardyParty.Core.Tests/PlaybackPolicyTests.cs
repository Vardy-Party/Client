using AutoFixture;
using VardyParty.Playback;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests;

public class PlaybackPolicyTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Theory]
    [InlineData(null, "http://a", false, true)]
    [InlineData(null, "http://a", true, false)]
    [InlineData("http://a", "http://a", false, false)]
    [InlineData("http://a", "HTTP://A", false, false)]
    [InlineData("http://a", "http://b", false, true)]
    [InlineData("http://a", "", false, false)]
    [InlineData("http://a", "  ", false, false)]
    public void CanAttach_MatchesLegacySwitchRules(string? current, string candidate, bool preparing, bool expected)
    {
        // Arrange
        var currentUrl = current;
        var candidateUrl = candidate;

        // Act
        var canAttach = PlaybackPolicy.CanAttach(currentUrl, candidateUrl, preparing);
        var canSwitch = SwitchingDecision.CanSwitch(currentUrl, candidateUrl ?? string.Empty, preparing);

        // Assert
        Assert.Equal(expected, canAttach);
        Assert.Equal(expected, canSwitch);
    }

    [Fact]
    public void CanUserNavigate_RequiresIdleEnoughAndPool()
    {
        // Arrange
        var playingIdle = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.State, PlaybackSessionState.Playing)
            .With(s => s.IsPreparing, false)
            .Create();
        var playingPreparing = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.State, PlaybackSessionState.Playing)
            .With(s => s.IsPreparing, true)
            .Create();
        var closed = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.State, PlaybackSessionState.Closed)
            .With(s => s.IsPreparing, false)
            .Create();

        // Act
        var cannotNavigateSolo = PlaybackPolicy.CanUserNavigate(playingIdle, healthyStreamCount: 1);
        var canNavigatePool = PlaybackPolicy.CanUserNavigate(playingIdle, healthyStreamCount: 2);
        var cannotNavigatePreparing = PlaybackPolicy.CanUserNavigate(playingPreparing, 5);
        var cannotNavigateClosed = PlaybackPolicy.CanUserNavigate(closed, 5);

        // Assert
        Assert.False(cannotNavigateSolo);
        Assert.True(canNavigatePool);
        Assert.False(cannotNavigatePreparing);
        Assert.False(cannotNavigateClosed);
    }

    [Fact]
    public void ShouldRetryFreshResolve_OnlyBeforeEstablishWithCache()
    {
        // Arrange
        var unusedCache = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.UsedCachedUrl, true)
            .With(s => s.CacheRetryUsed, false)
            .With(s => s.HasEstablishedPlayback, false)
            .Create();
        var alreadyRetried = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.UsedCachedUrl, true)
            .With(s => s.CacheRetryUsed, true)
            .With(s => s.HasEstablishedPlayback, false)
            .Create();
        var alreadyEstablished = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.UsedCachedUrl, true)
            .With(s => s.CacheRetryUsed, false)
            .With(s => s.HasEstablishedPlayback, true)
            .Create();

        // Act
        var shouldRetry = PlaybackPolicy.ShouldRetryFreshResolve(unusedCache);
        var shouldNotRetryAfterCache = PlaybackPolicy.ShouldRetryFreshResolve(alreadyRetried);
        var shouldNotRetryAfterEstablish = PlaybackPolicy.ShouldRetryFreshResolve(alreadyEstablished);

        // Assert
        Assert.True(shouldRetry);
        Assert.False(shouldNotRetryAfterCache);
        Assert.False(shouldNotRetryAfterEstablish);
    }

    [Fact]
    public void ShouldRevertAfterFailedSwitch_OnlyWhileSwitchingWithLastGood()
    {
        // Arrange
        var switchingWithLastGood = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.HasEstablishedPlayback, true)
            .With(s => s.State, PlaybackSessionState.Switching)
            .With(s => s.LastGoodUrl, "http://good")
            .Create();
        var playingWithLastGood = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.HasEstablishedPlayback, true)
            .With(s => s.State, PlaybackSessionState.Playing)
            .With(s => s.LastGoodUrl, "http://good")
            .Create();

        // Act
        var shouldRevert = PlaybackPolicy.ShouldRevertAfterFailedSwitch(switchingWithLastGood);
        var shouldNotRevert = PlaybackPolicy.ShouldRevertAfterFailedSwitch(playingWithLastGood);

        // Assert
        Assert.True(shouldRevert);
        Assert.False(shouldNotRevert);
    }

    [Fact]
    public void IsHealthDeclined_UsesSharedWindowThresholds()
    {
        // Arrange
        var empty = new StreamMetricsWindow();
        var threeBuffering = new StreamMetricsWindow();
        threeBuffering.AddBufferingEvent();
        threeBuffering.AddBufferingEvent();
        threeBuffering.AddBufferingEvent();
        var fourBuffering = new StreamMetricsWindow();
        fourBuffering.AddBufferingEvent();
        fourBuffering.AddBufferingEvent();
        fourBuffering.AddBufferingEvent();
        fourBuffering.AddBufferingEvent();

        // Act
        var emptyDeclined = PlaybackPolicy.IsHealthDeclined(empty);
        var threeDeclined = PlaybackPolicy.IsHealthDeclined(threeBuffering);
        var fourDeclined = PlaybackPolicy.IsHealthDeclined(fourBuffering);

        // Assert
        Assert.False(emptyDeclined);
        Assert.False(threeDeclined);
        Assert.True(fourDeclined);
    }

    [Fact]
    public void ShouldAdvanceAfterEstablishedFailure_RequiresPlayingPool()
    {
        // Arrange
        var playing = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.HasEstablishedPlayback, true)
            .With(s => s.State, PlaybackSessionState.Playing)
            .Create();
        var switching = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.HasEstablishedPlayback, true)
            .With(s => s.State, PlaybackSessionState.Switching)
            .Create();

        // Act
        var advanceWithPool = PlaybackPolicy.ShouldAdvanceAfterEstablishedFailure(playing, 2);
        var stayWithSolo = PlaybackPolicy.ShouldAdvanceAfterEstablishedFailure(playing, 1);
        var stayWhileSwitching = PlaybackPolicy.ShouldAdvanceAfterEstablishedFailure(switching, 3);

        // Assert
        Assert.True(advanceWithPool);
        Assert.False(stayWithSolo);
        Assert.False(stayWhileSwitching);
    }

    [Fact]
    public void ShouldAdvanceAfterFailedStart_WhenPoolRemainsAfterRemove()
    {
        // Arrange
        var start = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.HasEstablishedPlayback, false)
            .Create();
        var established = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.HasEstablishedPlayback, true)
            .Create();

        // Act
        var advanceWithPool = PlaybackPolicy.ShouldAdvanceAfterFailedStart(start, healthyStreamCountAfterRemove: 1);
        var stayWhenEmpty = PlaybackPolicy.ShouldAdvanceAfterFailedStart(start, healthyStreamCountAfterRemove: 0);
        var stayWhenEstablished = PlaybackPolicy.ShouldAdvanceAfterFailedStart(established, 2);

        // Assert
        Assert.True(advanceWithPool);
        Assert.False(stayWhenEmpty);
        Assert.False(stayWhenEstablished);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void IsHardDownloadFailure_MatchesWindowsThreshold(int failures, bool expected)
    {
        // Arrange
        var consecutiveFailures = failures;

        // Act
        var isHard = PlaybackPolicy.IsHardDownloadFailure(consecutiveFailures);

        // Assert
        Assert.Equal(expected, isHard);
    }

    [Fact]
    public void ShouldIgnoreClearedEngineError_LocksAndroidNullErrorNoOp()
    {
        // Arrange

        // Act
        var ignoreCleared = PlaybackPolicy.ShouldIgnoreClearedEngineError(errorIsNull: true);
        var keepRealError = PlaybackPolicy.ShouldIgnoreClearedEngineError(errorIsNull: false);

        // Assert
        Assert.True(ignoreCleared);
        Assert.False(keepRealError);
    }

    [Fact]
    public void IsCurrentGeneration_RejectsStaleEvents()
    {
        // Arrange
        var snap = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.AttachGeneration, 3)
            .Create();

        // Act
        var current = PlaybackPolicy.IsCurrentGeneration(snap, 3);
        var stale = PlaybackPolicy.IsCurrentGeneration(snap, 2);

        // Assert
        Assert.True(current);
        Assert.False(stale);
    }

    [Fact]
    public void CanUserNavigate_BlocksFailedState()
    {
        // Arrange
        var snap = _fixture.Build<PlaybackSessionSnapshot>()
            .With(s => s.State, PlaybackSessionState.Failed)
            .With(s => s.IsPreparing, false)
            .Create();

        // Act
        var canNavigate = PlaybackPolicy.CanUserNavigate(snap, 5);

        // Assert
        Assert.False(canNavigate);
    }

    [Theory]
    [InlineData("http://cached", "http://fresh", true)]
    [InlineData("http://cached", "HTTP://CACHED", false)]
    [InlineData("http://cached", "http://cached", false)]
    [InlineData("http://cached", null, false)]
    [InlineData("http://cached", "", false)]
    [InlineData("http://cached", "  ", false)]
    public void ShouldAcceptFreshM3U8_OnlyWhenUrlDiffers(string failed, string? fresh, bool expected)
    {
        // Arrange
        var failedCachedUrl = failed;
        var freshUrl = fresh;

        // Act
        var shouldAccept = PlaybackPolicy.ShouldAcceptFreshM3U8(failedCachedUrl, freshUrl);

        // Assert
        Assert.Equal(expected, shouldAccept);
    }

    [Fact]
    public void ShouldSkipCountdown_SkipsPlayableProbe()
    {
        // Arrange

        // Act
        var skipCountdown = PlaybackPolicy.ShouldSkipCountdown(true);
        var keepPlayable = PlaybackPolicy.ShouldSkipCountdown(false);

        // Assert
        Assert.True(skipCountdown);
        Assert.False(keepPlayable);
    }
}
