using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Next, Previous and auto-advance walk channel numbers in order (with wrap),
/// skipping only rows tried or failed this cycle (docs/PROBE_SCHEDULING_RULES.md
/// rule 11; docs/STREAM_PLAYBACK_RULES.md). Tiers do not change switch order:
/// they are for LocalService slot scheduling only. The prewarmer follows the
/// same order (<see cref="StreamSwitchingService.GetUpcoming"/>).
/// </summary>
public class BackupPreferenceBusinessRulesTests
{
    private readonly Dictionary<EnrichedStream, StreamTier> _tiers = new(ReferenceEqualityComparer.Instance);

    private StreamTier TierOf(EnrichedStream row) => _tiers.TryGetValue(row, out var tier) ? tier : StreamTier.Finding;

    private EnrichedStream Row(string channel, StreamTier tier = StreamTier.Finding)
    {
        var row = new EnrichedStream
        {
            Stream = new StreamModel { Url = $"https://{channel.ToLowerInvariant()}.example/match", Channel = channel },
            ResolvedM3U8Url = $"https://cdn.example/{channel.ToLowerInvariant()}/index.m3u8",
            Status = StreamResolutionStatus.Healthy,
        };
        _tiers[row] = tier;
        return row;
    }

    private static StreamSwitchingService Pool(params EnrichedStream[] rows)
    {
        var pool = new StreamSwitchingService();
        pool.Initialize("Premier League", "Home United", "Away City");
        foreach (var row in rows)
            pool.AddHealthyStream(row);
        return pool;
    }

    private static List<string> Channels(StreamSwitchingService pool) =>
        pool.GetHealthyStreams().Select(s => s.Stream.Channel!).ToList();

    [Fact]
    public void Rule_NextFromTheStartRow_GoesToTheNextChannelEvenWhenAHighIsLater()
    {
        // Arrange — #1 High plays; #2 is a finding row; #3 is High.
        var h1 = Row("H1", StreamTier.High);
        var f1 = Row("F1");
        var h2 = Row("H2", StreamTier.High);
        var pool = Pool(h1, f1, h2);
        pool.SetBackupPreference(h1, TierOf);

        // Act
        var upcoming = pool.GetUpcoming(1);
        var switched = pool.SwitchToNextStream();

        // Assert — Next lands on #2, and the channel numbers are unchanged.
        Assert.Same(f1, upcoming.Single());
        Assert.True(switched);
        Assert.Same(f1, pool.GetCurrentStream());
        Assert.Equal(["H1", "F1", "H2"], Channels(pool));
    }

    [Fact]
    public void Rule_ALaterBetterTierRow_DoesNotReplaceTheNextChannel()
    {
        // Arrange
        var h1 = Row("H1", StreamTier.High);
        var f1 = Row("F1");
        var m1 = Row("M1", StreamTier.Medium);
        var pool = Pool(h1, f1);
        pool.SetBackupPreference(h1, TierOf);
        var before = pool.GetNextHealthyStream();

        // Act
        pool.AddHealthyStream(m1);
        var after = pool.GetNextHealthyStream();

        // Assert — auto-advance and GetUpcoming stay on #2, even after a Medium joins as #3.
        Assert.Same(f1, before);
        Assert.Same(f1, after);
        Assert.Same(f1, pool.GetUpcoming(1).Single());
        Assert.Equal(["H1", "F1", "M1"], Channels(pool));
    }

    [Fact]
    public void Rule_FromAnyOtherRow_NextWalksChannelNumbers()
    {
        // Arrange
        var h1 = Row("H1", StreamTier.High);
        var f1 = Row("F1");
        var h2 = Row("H2", StreamTier.High);
        var pool = Pool(h1, f1, h2);
        pool.SetBackupPreference(h1, TierOf);

        // Act
        pool.SwitchToNextStream();
        var afterFirst = pool.GetCurrentStream();
        pool.SwitchToNextStream();
        var afterSecond = pool.GetCurrentStream();
        pool.SwitchToNextStream();

        // Assert — H1 -> F1 -> H2 -> wrap to H1.
        Assert.Same(f1, afterFirst);
        Assert.Same(h2, afterSecond);
        Assert.Same(h1, pool.GetCurrentStream());
    }

    [Fact]
    public void Rule_ASkippedNextChannel_IsPassedOver()
    {
        // Arrange
        var h1 = Row("H1", StreamTier.High);
        var f1 = Row("F1");
        var m1 = Row("M1", StreamTier.Medium);
        var h2 = Row("H2", StreamTier.High);
        var pool = Pool(h1, f1, m1, h2);
        pool.SetBackupPreference(h1, TierOf);
        pool.MarkTriedInCycle(f1, null, "No video (audio only)");

        // Act
        pool.SwitchToNextStream();

        // Assert — #2 was tried this cycle, so Next goes to #3.
        Assert.Same(m1, pool.GetCurrentStream());
    }

    [Fact]
    public void Rule_SetBackupPreference_DoesNotChangeSwitchOrder()
    {
        // Arrange — calling SetBackupPreference must not make Next jump to a later High.
        var h1 = Row("H1", StreamTier.High);
        var f1 = Row("F1");
        var h2 = Row("H2", StreamTier.High);
        var pool = Pool(h1, f1, h2);
        pool.SetBackupPreference(h1, null);

        // Act
        var next = pool.GetNextHealthyStream();
        pool.SetBackupPreference(h1, TierOf);
        var afterPreference = pool.GetNextHealthyStream();
        pool.Initialize("Premier League", "Home United", "Away City");
        pool.AddHealthyStream(h1);
        pool.AddHealthyStream(f1);
        pool.AddHealthyStream(h2);
        var afterNewSession = pool.GetNextHealthyStream();

        // Assert
        Assert.Same(f1, next);
        Assert.Same(f1, afterPreference);
        Assert.Same(f1, afterNewSession);
    }

    [Fact]
    public void Rule_WouldWrapOnNext_FollowsChannelOrder()
    {
        // Arrange — from #2 Next is #3 (no wrap); from #3 Next wraps to #1.
        var m1 = Row("M1", StreamTier.Medium);
        var h1 = Row("H1", StreamTier.High);
        var f2 = Row("F2");
        var pool = Pool(m1, h1, f2);
        pool.SwitchToStream(1);

        // Act / Assert
        Assert.False(pool.WouldWrapOnNext());
        Assert.Same(f2, pool.GetNextHealthyStream());
        Assert.True(pool.SwitchToPreviousStream());
        Assert.Same(m1, pool.GetCurrentStream());
        pool.SwitchToStream(2);
        Assert.True(pool.WouldWrapOnNext());
        Assert.Same(m1, pool.GetNextHealthyStream());
    }

    [Fact]
    public void Rule_GetUpcoming_MatchesWhereNextGoes()
    {
        // Arrange
        var h1 = Row("H1", StreamTier.High);
        var f1 = Row("F1");
        var h2 = Row("H2", StreamTier.High);
        var pool = Pool(h1, f1, h2);

        // Act
        var fromStart = pool.GetUpcoming(2).ToList();
        pool.SwitchToNextStream();
        var fromSecond = pool.GetUpcoming(2).ToList();

        // Assert
        Assert.Equal([f1, h2], fromStart);
        Assert.Equal([h2, h1], fromSecond);
    }
}
