using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Backup preference (docs/PROBE_SCHEDULING_RULES.md rule 11): while the start row (the
/// first to play) plays, the backup - where Next and auto-advance go, and what the
/// prewarmer warms first - is the second playable High row, then the first playable
/// Medium row, then the first playable finding row, whatever the channel numbers. The
/// numbers never change; from any other row Next walks them as before.
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
    public void Rule_NextFromTheStartRow_GoesToTheSecondHighAheadOfAnEarlierFindingRow()
    {
        // Arrange — #1 High plays; #2 is a finding row that turned playable first; #3 is High.
        var h1 = Row("H1", StreamTier.High);
        var f1 = Row("F1");
        var h2 = Row("H2", StreamTier.High);
        var pool = Pool(h1, f1, h2);
        pool.SetBackupPreference(h1, TierOf);

        // Act
        var upcoming = pool.GetUpcoming(1);
        var switched = pool.SwitchToNextStream();

        // Assert — Next lands on H2, and the channel numbers are unchanged.
        Assert.Same(h2, upcoming.Single());
        Assert.True(switched);
        Assert.Same(h2, pool.GetCurrentStream());
        Assert.Equal(["H1", "F1", "H2"], Channels(pool));
    }

    [Fact]
    public void Rule_ABetterTierRowThatTurnsPlayableLater_ReplacesAFindingBackup()
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

        // Assert
        Assert.Same(f1, before);
        Assert.Same(m1, after);
        Assert.Equal(["H1", "F1", "M1"], Channels(pool));
    }

    [Fact]
    public void Rule_FromAnyOtherRow_NextWalksChannelNumbers()
    {
        // Arrange — after the first Next the user is on H2 (#3); Next wraps to #1 as before.
        var h1 = Row("H1", StreamTier.High);
        var f1 = Row("F1");
        var h2 = Row("H2", StreamTier.High);
        var pool = Pool(h1, f1, h2);
        pool.SetBackupPreference(h1, TierOf);
        pool.SwitchToNextStream();

        // Act
        pool.SwitchToNextStream();
        var afterSecond = pool.GetCurrentStream();
        pool.SwitchToNextStream();

        // Assert — H2 -> H1 (wrap) -> back on the start row, so the backup again: H2.
        Assert.Same(h1, afterSecond);
        Assert.Same(h2, pool.GetCurrentStream());
    }

    [Fact]
    public void Rule_ABackupSkippedThisCycle_IsPassedOverForTheNextBestTier()
    {
        // Arrange
        var h1 = Row("H1", StreamTier.High);
        var f1 = Row("F1");
        var m1 = Row("M1", StreamTier.Medium);
        var h2 = Row("H2", StreamTier.High);
        var pool = Pool(h1, f1, m1, h2);
        pool.SetBackupPreference(h1, TierOf);
        pool.MarkTriedInCycle(h2, null, "No video (audio only)");

        // Act
        pool.SwitchToNextStream();

        // Assert
        Assert.Same(m1, pool.GetCurrentStream());
    }

    [Fact]
    public void Rule_NoPreference_NextWalksChannelNumbers()
    {
        // Arrange — no tiers (nothing recommended), or a new session.
        var h1 = Row("H1", StreamTier.High);
        var f1 = Row("F1");
        var h2 = Row("H2", StreamTier.High);
        var pool = Pool(h1, f1, h2);
        pool.SetBackupPreference(h1, null);

        // Act
        var next = pool.GetNextHealthyStream();
        pool.SetBackupPreference(h1, TierOf);
        pool.Initialize("Premier League", "Home United", "Away City");
        pool.AddHealthyStream(h1);
        pool.AddHealthyStream(f1);
        pool.AddHealthyStream(h2);
        var afterNewSession = pool.GetNextHealthyStream();

        // Assert
        Assert.Same(f1, next);
        Assert.Same(f1, afterNewSession);
    }

    [Fact]
    public void Rule_WouldWrapOnNext_FollowsTheBackup()
    {
        // Arrange — the start row is #2; the backup (Medium) is #1, so Next wraps.
        var m1 = Row("M1", StreamTier.Medium);
        var h1 = Row("H1", StreamTier.High);
        var f2 = Row("F2");
        var pool = Pool(m1, h1, f2);
        pool.SwitchToStream(1);
        pool.SetBackupPreference(h1, TierOf);

        // Act
        var wraps = pool.WouldWrapOnNext();
        var next = pool.GetNextHealthyStream();

        // Assert
        Assert.True(wraps);
        Assert.Same(m1, next);
    }
}
