using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Streaming;
using VardyParty.Streaming.Tests.Timeline;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming.Tests;

/// <summary>Rule 1 (docs/PROBE_SCHEDULING_RULES.md): which rows get the head start, and how long.</summary>
public class TopTierHeadStartTests
{
    private static StreamModel Row(string id) => new() { Url = $"https://fb.example.test/{id}", Channel = id };

    private static RecommendationResponse Recs(params (string Id, RecommendationConfidence Confidence)[] recs) => new()
    {
        HasData = true,
        Recommended = recs.Select(r => new RecommendationItem { Url = $"https://fb.example.test/{r.Id}", Confidence = r.Confidence }).ToList(),
    };

    [Fact]
    public void Duration_IsTwelveSeconds()
    {
        // Arrange
        var expected = TimeSpan.FromSeconds(12);

        // Act
        var actual = TopTierHeadStart.Duration;

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Select_HighThenMedium_LowIsFinding()
    {
        // Arrange — try-order puts High, then Medium, then Low first.
        var h1 = Row("h1");
        var h2 = Row("h2");
        var m1 = Row("m1");
        var l1 = Row("l1");
        var f1 = Row("f1");
        var recs = Recs(("h1", RecommendationConfidence.High), ("h2", RecommendationConfidence.High),
            ("m1", RecommendationConfidence.Medium), ("l1", RecommendationConfidence.Low));

        // Act
        var (high, medium) = TopTierHeadStart.Select(recs, [h1, h2, m1, l1, f1]);

        // Assert
        Assert.Equal([h1, h2], high);
        Assert.Equal([m1], medium);
    }

    [Fact]
    public void Select_NoHighOrMedium_Empty()
    {
        // Arrange
        var l1 = Row("l1");
        var f1 = Row("f1");

        // Act
        var none = TopTierHeadStart.Select(null, [l1, f1]);
        var lowOnly = TopTierHeadStart.Select(Recs(("l1", RecommendationConfidence.Low)), [l1, f1]);

        // Assert
        Assert.Empty(none.High);
        Assert.Empty(none.Medium);
        Assert.Empty(lowOnly.High);
        Assert.Empty(lowOnly.Medium);
    }

    [Fact]
    public void Select_MpChipRecommendation_MatchesItsPageRow()
    {
        // Arrange — the recommendation names a chip; the catalog row is the unlabeled page.
        var page = new StreamModel { Url = "https://mp.example.test/mp", Channel = "", ResolutionStrategy = "v2", Source = "mp" };
        var recs = new RecommendationResponse
        {
            HasData = true,
            Recommended = [new RecommendationItem { Url = page.Url, StreamName = "Chip A", Confidence = RecommendationConfidence.High }],
        };

        // Act
        var (high, _) = TopTierHeadStart.Select(recs, [page]);

        // Assert
        Assert.Equal([page], high);
    }

    [Fact]
    public async Task Begin_SetsCurrentUntilDisposed_ElapsesOnTheClock()
    {
        // Arrange
        var clock = new MilestoneClock(DateTimeOffset.UnixEpoch);
        var throttle = new ResolverRaceThrottle();
        var h1 = Row("h1");
        var m1 = Row("m1");

        // Act
        var headStart = throttle.BeginTopTierHeadStart([h1], [m1], clock)!;
        var current = throttle.CurrentHeadStart;
        var elapsedBefore = headStart.IsElapsed;
        clock.AdvanceToNext();
        await headStart.Elapsed.WaitAsync(TimeSpan.FromSeconds(5));
        var elapsedAfter = headStart.IsElapsed;
        var at = clock.GetUtcNow() - DateTimeOffset.UnixEpoch;
        headStart.Dispose();

        // Assert
        Assert.Same(headStart, current);
        Assert.True(headStart.IsTopTier(h1));
        Assert.True(headStart.IsMedium(m1));
        Assert.False(headStart.IsTopTier(m1));
        Assert.False(elapsedBefore);
        Assert.True(elapsedAfter);
        Assert.Equal(TopTierHeadStart.Duration, at);
        Assert.Null(throttle.CurrentHeadStart);
    }

    [Fact]
    public void Begin_NoRows_ReturnsNull()
    {
        // Arrange
        var throttle = new ResolverRaceThrottle();

        // Act
        var headStart = throttle.BeginTopTierHeadStart([], [], TimeProvider.System);

        // Assert
        Assert.Null(headStart);
        Assert.Null(throttle.CurrentHeadStart);
    }

    [Fact]
    public void CurrentTiers_OutliveTheHeadStart_UntilANewSessionWithoutTiers()
    {
        // Arrange — "Medium limited to one slot always": later passes of the session
        // (Deferred re-queues, CDN rechecks) still read the tiers after the head start.
        var throttle = new ResolverRaceThrottle();
        var m1 = Row("m1");
        var headStart = throttle.BeginTopTierHeadStart([], [m1], TimeProvider.System)!;

        // Act
        headStart.Dispose();
        var afterHeadStart = throttle.CurrentTiers;
        throttle.BeginTopTierHeadStart([], [], TimeProvider.System);
        var nextSession = throttle.CurrentTiers;

        // Assert
        Assert.Null(throttle.CurrentHeadStart);
        Assert.Same(headStart.Tiers, afterHeadStart);
        Assert.True(afterHeadStart!.IsMedium(m1));
        Assert.Null(nextSession);
    }

    [Fact]
    public void Tiers_ChipsOfAHighPage_AreHigh_AndOfAMediumPage_Medium()
    {
        // Arrange — rule 1: chips LocalService lists for a recommended MP page take its tier.
        var highPage = Row("mp-high");
        var mediumPage = Row("mp-medium");
        var findingPage = Row("mp-finding");
        var tiers = new StreamTiers([highPage], [mediumPage]);
        var highChip = Row("mp-high-b");
        var mediumChip = Row("mp-medium-b");
        var findingChip = Row("mp-finding-b");

        // Act
        tiers.Inherit(highPage, [highChip]);
        tiers.Inherit(mediumPage, [mediumChip]);
        tiers.Inherit(findingPage, [findingChip]);

        // Assert
        Assert.Equal(StreamTier.High, tiers.TierOf(highChip));
        Assert.Equal(StreamTier.Medium, tiers.TierOf(mediumChip));
        Assert.Equal(StreamTier.Finding, tiers.TierOf(findingChip));
        Assert.Equal(2, tiers.HighCount);
        Assert.Equal(2, tiers.MediumCount);
    }

    [Fact]
    public void Tiers_AChipThatAlreadyHasATier_KeepsIt()
    {
        // Arrange — a chip recommended High on its own stays High under a Medium page.
        var mediumPage = Row("mp-medium");
        var chip = Row("mp-medium-a");
        var tiers = new StreamTiers([chip], [mediumPage]);

        // Act
        tiers.Inherit(mediumPage, [chip]);

        // Assert
        Assert.Equal(StreamTier.High, tiers.TierOf(chip));
        Assert.Equal(1, tiers.MediumCount);
    }
}
