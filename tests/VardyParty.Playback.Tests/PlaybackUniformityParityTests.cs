using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Phase 3 / 4 parity (docs/PLAYBACK_UNIFORMITY_PLAN.md). The shared rules are
/// tested by behaviour, and a source scan checks that every platform adapter
/// (Android, Windows, Linux) reaches them through the shared code instead of its
/// own copy, so a platform cannot silently drift.
/// </summary>
public class PlaybackUniformityParityTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 20, 0, 0, TimeSpan.Zero);

    // ---- Behaviour of the shared rules ----

    [Fact]
    public void NoVideoTimeout_FiresAtTheSharedTimeout_NotBefore()
    {
        // Arrange
        var atTimeout = Start + PlaybackPolicy.NoVideoTimeout;
        var justBefore = atTimeout - TimeSpan.FromMilliseconds(1);

        // Act
        var firesAtTimeout = PlaybackPolicy.HasNoVideoTimedOut(Start, atTimeout, PlaybackPolicy.NoVideoTimeout);
        var firesJustBefore = PlaybackPolicy.HasNoVideoTimedOut(Start, justBefore, PlaybackPolicy.NoVideoTimeout);

        // Assert
        Assert.True(firesAtTimeout);
        Assert.False(firesJustBefore);
        Assert.Equal(TimeSpan.FromSeconds(8), PlaybackPolicy.NoVideoTimeout);
    }

    [Fact]
    public void DecodedPictureSettler_PublishesExactlyAfterTheSharedSettle()
    {
        // Arrange
        var now = Start;
        var sut = new DecodedPictureSizeSettler(() => now);
        sut.Observe(1280, 720);

        // Act
        now += PlaybackPolicy.DecodedPictureSettle - TimeSpan.FromMilliseconds(1);
        var beforeSettle = sut.Tick();
        now += TimeSpan.FromMilliseconds(1);
        var atSettle = sut.Tick();

        // Assert
        Assert.Null(beforeSettle);
        Assert.Equal((1280, 720), atSettle);
    }

    [Fact]
    public void LibVlcNetworkCaching_IsAPreRollCache_NotThe25SecondLiveOffset()
    {
        // Arrange — mapping DesiredLiveOffsetSeconds onto --network-caching would
        // delay the first picture by about 25s.
        var liveOffsetMs = PlaybackPolicy.DesiredLiveOffsetSeconds * 1000;

        // Act
        var desktop = PlaybackPolicy.ResolveLibVlcNetworkCachingMs(conservativeTestHost: false);
        var testHost = PlaybackPolicy.ResolveLibVlcNetworkCachingMs(conservativeTestHost: true);

        // Assert
        Assert.NotEqual(liveOffsetMs, desktop);
        Assert.NotEqual(liveOffsetMs, testHost);
        Assert.True(desktop < testHost);
        Assert.True(testHost < liveOffsetMs);
    }

    // ---- Source scan: every adapter uses the shared code ----

    public static TheoryData<string> Platforms => new() { "Android", "Windows", "Linux" };

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Adapter_WiresSwitchBackThroughTheSharedRunner_WithHeightAndUiThread(string platform)
    {
        // Arrange
        var source = AdapterSource(platform);

        // Act
        var create = Regex.Match(source, @"SwitchBackRunner\.Create\((?<args>[^;]*)\);", RegexOptions.Singleline);

        // Assert
        Assert.True(create.Success, $"{platform} does not create the shared SwitchBackRunner");
        Assert.Matches(@"settledHeight:\s*\(\)\s*=>\s*\w*[pP]ictureSizeSettler\.Published\?\.Height", create.Groups["args"].Value);
        Assert.Contains("runOnUi: SwitchBackRunner.Dispatch(", create.Groups["args"].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("new SwitchBackLoop", source, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Adapter_UsesTheSharedPictureSizeSettler_AndHealthMetricsInterval(string platform)
    {
        // Arrange
        var source = AdapterSource(platform);

        // Act
        var usesSettler = Regex.IsMatch(source, @"DecodedPictureSizeSettler\s+\w*[pP]ictureSizeSettler\s*=\s*new");
        var usesMetricsInterval = source.Contains("PlaybackPolicy.HealthMetricsInterval", StringComparison.Ordinal);

        // Assert
        Assert.True(usesSettler, $"{platform} does not use DecodedPictureSizeSettler");
        Assert.True(usesMetricsInterval, $"{platform} does not use PlaybackPolicy.HealthMetricsInterval");
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Adapter_RetriesPlaylistReloadsThroughTheSharedCoordinator(string platform)
    {
        // Arrange
        var source = AdapterSource(platform);

        // Act
        var usesCoordinator = Regex.IsMatch(source, @"PlaylistReloadCoordinator\.Run(Async)?\(");

        // Assert
        Assert.True(usesCoordinator, $"{platform} does not run option 1 through PlaylistReloadCoordinator");
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Adapter_NeverReplaysWatchedVideo_NoPerPlatformSoftRecovery(string platform)
    {
        // Arrange
        var source = AdapterSource(platform);

        // Act
        var replays = new[] { "SoftRecoverPlaybackAsync", "TryRecoverLiveHlsFailure", "ServeLastGood" }
            .Where(name => source.Contains(name, StringComparison.Ordinal))
            .ToList();

        // Assert
        Assert.Empty(replays);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Adapter_RecordsPlayedSegmentsAndTrimsEveryAttach_ThroughSharedMemory(string platform)
    {
        // Arrange
        var source = AdapterSource(platform);

        // Act
        var notesSegments = source.Contains("NoteSegmentFetched(", StringComparison.Ordinal);
        var sharesMemory = source.Contains("PlayedSegments", StringComparison.Ordinal);
        var passesPlaylistUrl = source.Contains("playlistUrl:", StringComparison.Ordinal);
        var ownCopy = Regex.IsMatch(source, @"class\s+PlayedSegmentMemory\b")
            || source.Contains("TrimPlayedPrefix(", StringComparison.Ordinal);

        // Assert
        Assert.True(notesSegments, $"{platform} does not record fetched segments via PlaylistReloadSession.NoteSegmentFetched");
        Assert.True(sharesMemory, $"{platform} does not hand PlaybackSessionController.PlayedSegments to its attach session");
        Assert.True(passesPlaylistUrl, $"{platform} does not pass playlistUrl to the shared coordinator");
        Assert.False(ownCopy, $"{platform} keeps its own played-segment trimming instead of the shared one");
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Adapter_FollowsMidPlayHeightChanges_ThroughTheSharedSettler(string platform)
    {
        // Arrange
        var source = AdapterSource(platform);

        // Act
        var remembers = source.Contains("MeasuredPictureSize.Remember(", StringComparison.Ordinal);
        var ticksOnChange = source.Contains("NeedsTick", StringComparison.Ordinal);

        // Assert
        Assert.True(remembers, $"{platform} does not copy the settled height into the stream's stored height");
        Assert.True(ticksOnChange, $"{platform} does not tick the settler while a new size is pending");
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Adapter_ColoursTheStreamToastChip_WithTheSharedBadgeStyle(string platform)
    {
        // Arrange
        var source = platform == "Linux"
            ? AdapterSource(platform) + File.ReadAllText(Path.Combine(RepoRoot(), "VardyParty.Linux", "Pages", "LinuxHomePage.PlayerChrome.cs"))
            : AdapterSource(platform);

        // Act
        var splitsChip = source.Contains("SplitSourceChip(", StringComparison.Ordinal)
            || source.Contains(".Parts", StringComparison.Ordinal);
        var usesSharedStyle = source.Contains("ChipStyle", StringComparison.Ordinal);

        // Assert
        Assert.True(splitsChip, $"{platform} does not split the toast around the shared chip");
        Assert.True(usesSharedStyle, $"{platform} does not colour the toast chip with SourceBadgeStyle");
    }

    private static string AdapterSource(string platform)
    {
        var root = RepoRoot();
        IEnumerable<string> files = platform switch
        {
            "Android" => Directory.EnumerateFiles(Path.Combine(root, "VardyParty", "Platforms", "Android"), "*.cs", SearchOption.AllDirectories),
            "Windows" => Directory.EnumerateFiles(Path.Combine(root, "VardyParty", "Platforms", "Windows"), "*.cs", SearchOption.AllDirectories),
            "Linux" => Directory.EnumerateFiles(Path.Combine(root, "VardyParty.Linux", "Services"), "*.cs", SearchOption.AllDirectories),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
        };
        return string.Join('\n', files.OrderBy(f => f, StringComparer.Ordinal).Select(File.ReadAllText));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VardyParty.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("VardyParty.slnx not found above the test output folder");
    }
}
