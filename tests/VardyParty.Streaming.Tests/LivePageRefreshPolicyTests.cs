using System;
using VardyParty.Kernel;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming.Tests;

public class LivePageRefreshPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DelayBeforeNextSweep_FewerThanTwoStreams_DoesNotRest(int working)
    {
        // Arrange
        var untilKickoff = TimeSpan.FromMinutes(20);

        // Act
        var delay = LivePageRefreshPolicy.DelayBeforeNextSweep(working, untilKickoff, viewerInPlayer: true);

        // Assert — scanning keeps going until a backup exists, including before kickoff.
        Assert.Equal(TimeSpan.Zero, delay);
        Assert.True(LivePageRefreshPolicy.KeepScanning(working));
    }

    [Fact]
    public void DelayBeforeNextSweep_TwoStreams_RestsFiveMinutes()
    {
        // Arrange
        const int working = 2;

        // Act
        var delay = LivePageRefreshPolicy.DelayBeforeNextSweep(working, TimeSpan.Zero, viewerInPlayer: true);

        // Assert
        Assert.Equal(LivePageRefreshPolicy.RestAfterBackup, delay);
        Assert.Equal(TimeSpan.FromMinutes(5), delay);
        Assert.False(LivePageRefreshPolicy.KeepScanning(working));
    }

    [Fact]
    public void DelayBeforeNextSweep_WatchingBeforeKickoffWithABackup_SweepsAtKickoff()
    {
        // Arrange — a pre-match backup is already in hand. The next sweep is the kickoff, not a five-minute rest.
        var untilKickoff = TimeSpan.FromMinutes(12);

        // Act
        var delay = LivePageRefreshPolicy.DelayBeforeNextSweep(2, untilKickoff, viewerInPlayer: true);

        // Assert
        Assert.Equal(untilKickoff, delay);
    }

    [Fact]
    public void DelayBeforeNextSweep_BackupInHandButViewerIsNotInThePlayer_RestsFiveMinutes()
    {
        // Arrange
        var untilKickoff = TimeSpan.FromMinutes(12);

        // Act
        var delay = LivePageRefreshPolicy.DelayBeforeNextSweep(2, untilKickoff, viewerInPlayer: false);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(5), delay);
    }

    [Fact]
    public void UntilKickoff_BeforeStart_IsTheRemainingTime()
    {
        // Arrange
        var now = new DateTime(2026, 11, 27, 19, 0, 0, DateTimeKind.Utc);
        var game = new Game { Start = now.AddMinutes(8) };

        // Act
        var until = LivePageRefreshPolicy.UntilKickoff(game, now);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(8), until);
    }

    [Fact]
    public void UntilKickoff_AfterStartOrFinishedOrUnknown_IsZero()
    {
        // Arrange
        var now = new DateTime(2026, 11, 27, 19, 0, 0, DateTimeKind.Utc);
        var started = new Game { Start = now.AddMinutes(-1) };
        var finished = new Game { Start = now.AddMinutes(10), IsFinished = true };
        var unknown = new Game();

        // Act
        var afterStart = LivePageRefreshPolicy.UntilKickoff(started, now);
        var afterFinish = LivePageRefreshPolicy.UntilKickoff(finished, now);
        var noStart = LivePageRefreshPolicy.UntilKickoff(unknown, now);

        // Assert
        Assert.Equal(TimeSpan.Zero, afterStart);
        Assert.Equal(TimeSpan.Zero, afterFinish);
        Assert.Equal(TimeSpan.Zero, noStart);
    }

    [Fact]
    public void PagesToAskAgain_OneRowPerPage_RechecksPlayableFbAndMp()
    {
        // Arrange
        var fbLive = Page("https://fb.example/live", source: "fb", status: "ready", channel: "North");
        var fbCountdown = Page("https://fb.example/soon", source: "fb", status: "countdown", channel: "Soon");
        var mpChipA = Page("https://mp.example/match", source: "mp", status: "ready", channel: "HD");
        mpChipA.PlayerStream = "HD";
        var mpChipB = Page("https://mp.example/match", source: "mp", status: "ready", channel: "SD");
        mpChipB.PlayerStream = "SD";

        // Act
        var pages = LivePageRefreshPolicy.PagesToAskAgain([fbLive, fbCountdown, mpChipA, mpChipB]);

        // Assert — a playable FB page is asked again, so a later sweep can see it die.
        // Countdown is asked with the API flag cleared. Both MP chips are one unlabeled page.
        Assert.Equal(3, pages.Count);
        Assert.Equal("https://fb.example/live", pages[0].Url);
        Assert.False(pages[0].IsCountdown);
        Assert.Equal("https://fb.example/soon", pages[1].Url);
        Assert.False(pages[1].IsCountdown);
        Assert.Equal("https://mp.example/match", pages[2].Url);
        Assert.True(pages[2].RequiresV2StreamSelection);
        Assert.True(string.IsNullOrEmpty(pages[2].PlayerStream));
        Assert.Empty(pages[2].PlayerStreams);
        Assert.Equal(1, LivePageRefreshPolicy.SlotsPerSweep);
    }

    [Fact]
    public void SwitchingAfterPageProbe_FailedPage_SkipsThatRow_DeferredDoesNot()
    {
        // Arrange
        var north = PoolRow("https://fb.example/north", "fb", channel: "North");
        var south = PoolRow("https://fb.example/south", "fb", channel: "South");

        // Act
        var failed = LivePageRefreshPolicy.SwitchingAfterPageProbe(
            [north, south],
            [Verdict("https://fb.example/north", StreamResolutionStatus.Failed)]);
        var deferred = LivePageRefreshPolicy.SwitchingAfterPageProbe(
            [north, south],
            [Verdict("https://fb.example/north", StreamResolutionStatus.Deferred)]);

        // Assert
        Assert.Same(north, Assert.Single(failed.Skip));
        Assert.Empty(failed.Restore);
        Assert.Empty(deferred.Skip);
        Assert.Empty(deferred.Restore);
    }

    [Fact]
    public void SwitchingAfterPageProbe_MissingChipIsSkipped_HealthyChipIsRestored_DeferredChipStays()
    {
        // Arrange
        var hd = PoolRow("https://mp.example/match", "mp", chip: "HD");
        var sd = PoolRow("https://mp.example/match", "mp", chip: "SD");
        var low = PoolRow("https://mp.example/match", "mp", chip: "Low");

        // Act — HD is healthy again, SD was not listed, Low's answer was deferred.
        var update = LivePageRefreshPolicy.SwitchingAfterPageProbe(
            [hd, sd, low],
            [
                Verdict("https://mp.example/match", StreamResolutionStatus.Healthy, "HD"),
                Verdict("https://mp.example/match", StreamResolutionStatus.Deferred, "Low")
            ]);

        // Assert
        Assert.Same(hd, Assert.Single(update.Restore));
        Assert.Same(sd, Assert.Single(update.Skip));
    }

    private static StreamModel Page(string url, string source, string status, string channel) =>
        new()
        {
            Url = url,
            Source = source,
            ResolutionStrategy = source == "mp" ? "v2" : "direct",
            StreamStatus = status,
            Channel = channel
        };

    private static EnrichedStream PoolRow(string url, string source, string? channel = null, string? chip = null) =>
        new()
        {
            Status = StreamResolutionStatus.Healthy,
            Stream = new StreamModel
            {
                Url = url,
                Source = source,
                ResolutionStrategy = source == "mp" ? "v2" : "direct",
                Channel = chip ?? channel ?? string.Empty,
                PlayerStream = chip ?? string.Empty
            }
        };

    private static EnrichedStream Verdict(string url, StreamResolutionStatus status, string? chip = null) =>
        new()
        {
            Status = status,
            Stream = new StreamModel
            {
                Url = url,
                Source = chip == null ? "fb" : "mp",
                ResolutionStrategy = chip == null ? "direct" : "v2",
                Channel = chip ?? "North",
                PlayerStream = chip ?? string.Empty
            }
        };
}
