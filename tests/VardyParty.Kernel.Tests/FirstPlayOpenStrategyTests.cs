using System;
using System.Collections.Generic;
using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

/// <summary>
/// Unit coverage for <see cref="FirstPlayOpenStrategy"/> — every PlanStart /
/// DecideAfter / PlanNext / priority helper path the hosts execute.
/// </summary>
public class FirstPlayOpenStrategyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793137000);

    // --- PlanStart -----------------------------------------------------------

    [Fact]
    public void PlanStart_ReusableRecommended_UseExisting()
    {
        // Arrange
        var row = Plain("https://cdn.example.com/a.m3u8?e=1893456000");

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(row, [row], Now);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.UseExisting, plan.Kind);
        Assert.Null(plan.PartnerToWarm);
        Assert.Contains("without fresh", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanStart_MpHarvestProbeReady_UseExisting_NoPartnerWarm()
    {
        // Arrange — /mp already streamed playlist + rewritten segments; token still comfortable.
        var mp = MpHarvest("Channel MP Alpha", M3u8TokenExpiryTests.UzhiUrl);
        var fb = Soak("Channel East");

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(mp, [mp, fb], Now);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.UseExisting, plan.Kind);
        Assert.Null(plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_MpHarvestUnreadableExpiry_PlaysProbedLink()
    {
        // Arrange — the /mp harvest link was just probed: first play uses it (rule 12),
        // no second /play; a failed start re-/plays once in the orchestrator.
        var mp = MpHarvest("Channel MP Alpha", "http://oak-lane.m3u8");
        var fb = Soak("Channel East");

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(mp, [mp, fb], Now);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.PlayProbedLink, plan.Kind);
        Assert.Null(plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_SoakRowWithProbedLink_PlaysProbedLink_NoSecondPlay()
    {
        // Arrange — Referer soak row the probe just resolved (unreadable expiry).
        var start = Soak("Channel North");
        start.ResolvedM3U8Url = "https://cdn.example.com/north.m3u8";
        var backup = Soak("Channel East");

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(start, [start, backup], Now);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.PlayProbedLink, plan.Kind);
        Assert.Null(plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_CookieBoundProbedLink_FreshPlay_WarmsBackup()
    {
        // Arrange — a cookie-bound FB session is single-use: the probe burned it.
        var start = CookieFb("Channel North");
        start.ResolvedM3U8Url = "https://cdn.example.com/north.m3u8";
        var backup = Soak("Channel East");

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(start, [start, backup], Now);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
        Assert.Same(backup, plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_ExpiredProbedLink_FreshPlay()
    {
        // Arrange — readable token already expired.
        var start = Soak("Channel North");
        start.ResolvedM3U8Url = "https://cdn.example.com/north.m3u8?e=1700000000";

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(start, [start], Now);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("https://cdn.example.com/a.m3u8", false, true)]
    [InlineData("https://cdn.example.com/a.m3u8?e=1893456000", false, true)]
    [InlineData("https://cdn.example.com/a.m3u8?e=1700000000", false, false)]
    [InlineData("https://cdn.example.com/a.m3u8", true, false)]
    public void CanPlayProbedLink_Table(string? url, bool cookie, bool expected)
    {
        // Arrange
        var row = cookie ? CookieFb("Channel A") : Soak("Channel A");
        row.ResolvedM3U8Url = url;

        // Act
        var actual = FirstPlayOpenStrategy.CanPlayProbedLink(row, Now);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PlanStart_TwoFbHealthy_OpensRecommended_WarmsNextInSwitchOrder()
    {
        // Arrange — two healthy soak rows: open first, warm second for Next.
        var rec = Soak("Channel North");
        var partner = Soak("Channel East");

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(rec, [rec, partner], Now);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
        Assert.Same(partner, plan.PartnerToWarm);
        Assert.Contains("Channel East", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanStart_ThreeHealthy_PartnerIsImmediateNextOnly()
    {
        // Arrange
        var a = Soak("Channel West");
        var b = Soak("Channel South");
        var c = Soak("Channel Delta");

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(a, [a, b, c], Now);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
        Assert.Same(b, plan.PartnerToWarm);
        Assert.NotSame(c, plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_NeedsFresh_NoPartner_StillOpenRecommendedFirst()
    {
        // Arrange
        var rec = Soak("Channel Solo");

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(rec, [rec], Now);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
        Assert.Null(plan.PartnerToWarm);
        Assert.Contains("no partner", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanStart_RecommendedAlreadyWarmOrInFlight_UseExisting()
    {
        // Arrange
        var rec = Soak("Channel North");
        var partner = Soak("Channel South");

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(
            rec, [rec, partner], Now, isWarmOrInFlight: r => ReferenceEquals(r, rec));

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.UseExisting, plan.Kind);
        Assert.Null(plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_PartnerAlreadyWarm_OpensRecommended_WithoutPartnerWarm()
    {
        // Arrange
        var rec = Soak("Channel North");
        var partner = Soak("Channel South");

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(
            rec, [rec, partner], Now, isWarmOrInFlight: r => ReferenceEquals(r, partner));

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
        Assert.Null(plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_CachedReusablePartner_NotPickedForWarm()
    {
        // Arrange
        var rec = Soak("Channel North");
        var cached = Plain("https://cdn.example.com/east.m3u8?e=1893456000");
        cached.Stream!.Channel = "Channel East";

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(rec, [rec, cached], Now);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
        // PickPartner requires NeedsFreshPlay on partner — cached row is skipped.
        Assert.Null(plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_NullArgs_Throw()
    {
        // Arrange
        var row = Soak("Channel X");

        // Act / Assert
        Assert.Throws<ArgumentNullException>(() =>
            FirstPlayOpenStrategy.PlanStart(null!, [row], Now));
        Assert.Throws<ArgumentNullException>(() =>
            FirstPlayOpenStrategy.PlanStart(row, null!, Now));
    }

    // --- DecideAfterRecommended / warm / verdict ----------------------------

    [Theory]
    [InlineData(RaceLegState.Video, FirstPlayOpenStrategy.AfterRecommendedKind.OpenRecommended)]
    [InlineData(RaceLegState.Unverified, FirstPlayOpenStrategy.AfterRecommendedKind.OpenRecommended)]
    [InlineData(RaceLegState.Empty, FirstPlayOpenStrategy.AfterRecommendedKind.AwaitPartnerOrCached)]
    [InlineData(RaceLegState.AudioOnly, FirstPlayOpenStrategy.AfterRecommendedKind.AwaitPartnerOrCached)]
    [InlineData(RaceLegState.Infrastructure, FirstPlayOpenStrategy.AfterRecommendedKind.Fail)]
    [InlineData(RaceLegState.Pending, FirstPlayOpenStrategy.AfterRecommendedKind.Fail)]
    public void DecideAfterRecommended_Table(
        RaceLegState state,
        FirstPlayOpenStrategy.AfterRecommendedKind expected)
    {
        // Arrange
        _ = state;

        // Act
        var kind = FirstPlayOpenStrategy.DecideAfterRecommended(state);

        // Assert
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(RaceLegState.Video, true)]
    [InlineData(RaceLegState.Unverified, true)]
    [InlineData(RaceLegState.Empty, false)]
    [InlineData(RaceLegState.AudioOnly, false)]
    [InlineData(RaceLegState.Infrastructure, false)]
    [InlineData(RaceLegState.Pending, false)]
    public void ShouldWarmPartnerLeg_Table(RaceLegState state, bool expected)
    {
        // Arrange
        _ = state;

        // Act
        var warm = FirstPlayOpenStrategy.ShouldWarmPartnerLeg(state);

        // Assert
        Assert.Equal(expected, warm);
    }

    [Theory]
    [InlineData(RaceLegState.Empty, true)]
    [InlineData(RaceLegState.AudioOnly, true)]
    [InlineData(RaceLegState.Video, false)]
    [InlineData(RaceLegState.Unverified, false)]
    [InlineData(RaceLegState.Infrastructure, false)]
    public void IsStreamVerdict_Table(RaceLegState state, bool expected)
    {
        // Arrange
        _ = state;

        // Act
        var verdict = FirstPlayOpenStrategy.IsStreamVerdict(state);

        // Assert
        Assert.Equal(expected, verdict);
    }

    // --- PlanNextPrepare (early Next / switch) -------------------------------

    [Fact]
    public void PlanNextPrepare_WarmReady_ApplyWarmUnused_EvenWithBurnedFindUrl()
    {
        // Arrange — Next onto warm partner after open-recommended; avoid ResolveFresh.
        var next = Soak("Channel East");
        next.ResolvedM3U8Url = "https://cdn.example.com/burned.m3u8";

        // Act
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: true);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.ApplyWarmUnused, kind);
    }

    [Fact]
    public void PlanNextPrepare_UnusedPlaybackUrl_AttachAsIs()
    {
        // Arrange
        var next = Soak("Channel East");
        next.ResolvedM3U8Url = "https://cdn.example.com/fresh.m3u8";
        next.UnusedResolvedUrl = true;

        // Act
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.AttachAsIs, kind);
    }

    [Fact]
    public void PlanNextPrepare_MpHarvestCanReuse_AttachAsIs()
    {
        // Arrange — long-lived harvest URL still has MinComfortableLife.
        var next = MpHarvest("Channel MP Beta", M3u8TokenExpiryTests.UzhiUrl);

        // Act
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.AttachAsIs, kind);
    }

    [Fact]
    public void PlanNextPrepare_MpHarvestUnreadableExpiry_ClearAndResolveFresh()
    {
        // Arrange — unreadable harvest without UnusedResolvedUrl forces a fresh /play.
        var next = MpHarvest("Channel MP Beta", "http://oak-lane.m3u8");

        // Act
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.ClearAndResolveFresh, kind);
    }

    [Fact]
    public void PlanNextPrepare_FbCookieOneShot_NoWarm_ClearAndResolveFresh()
    {
        // Arrange
        var next = CookieFb("Channel Cookie");
        next.ResolvedM3U8Url = "https://cdn.example.com/burned.m3u8";

        // Act
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.ClearAndResolveFresh, kind);
    }

    [Fact]
    public void PlanNextPrepare_RefererSoak_NoWarm_ClearAndResolveFresh()
    {
        // Arrange
        var next = Soak("Channel West");
        next.ResolvedM3U8Url = "https://cdn.example.com/burned.m3u8";

        // Act
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.ClearAndResolveFresh, kind);
    }

    [Fact]
    public void PlanNextPrepare_EmptyUrl_NonSoak_ClearAndResolveFresh()
    {
        // Arrange
        var next = Plain(null);

        // Act
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.ClearAndResolveFresh, kind);
    }

    [Fact]
    public void PlanNextPrepare_PlainCachedUrl_AttachAsIs()
    {
        // Arrange
        var next = Plain("https://cdn.example.com/plain.m3u8");

        // Act
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.AttachAsIs, kind);
    }

    [Fact]
    public void PlanNextPrepare_WarmReadyBeatsBurnedFind_OverClear()
    {
        // Arrange
        var next = Soak("Channel X");
        next.ResolvedM3U8Url = "https://cdn.example.com/burned/";

        // Act
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: true);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.ApplyWarmUnused, kind);
    }

    // --- Playback priority helpers -----------------------------------------

    [Fact]
    public void PlaybackPriorityTimeout_IsThirtySeconds()
    {
        // Arrange
        var expected = TimeSpan.FromSeconds(30);

        // Act
        var actual = FirstPlayOpenStrategy.PlaybackPriorityTimeout;

        // Assert
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void PlaybackPriorityHolds_OnlyWhileABackupNeedsWarming(int needingWarm, bool expected)
    {
        // Arrange
        _ = needingWarm;

        // Act
        var holds = FirstPlayOpenStrategy.PlaybackPriorityHolds(needingWarm);

        // Assert
        Assert.Equal(expected, holds);
    }

    [Fact]
    public void CountBackupsNeedingWarm_NoBackup_Zero()
    {
        // Arrange — rule 11: a single healthy row has no backup to keep warm.
        var playing = Soak("Channel Playing");

        // Act
        var n = FirstPlayOpenStrategy.CountBackupsNeedingWarm([playing], playing, Now, _ => null, _ => false);

        // Assert
        Assert.Equal(0, n);
    }

    [Fact]
    public void CountBackupsNeedingWarm_SoakBackupNotWarm_One()
    {
        // Arrange
        var playing = Soak("Channel Playing");
        var backup = Soak("Channel Backup");

        // Act
        var n = FirstPlayOpenStrategy.CountBackupsNeedingWarm([playing, backup], playing, Now, _ => null, _ => false);

        // Assert
        Assert.Equal(1, n);
    }

    [Fact]
    public void CountBackupsNeedingWarm_WarmCookieUnwarmableOrMpBackups_Zero()
    {
        // Arrange — warm already, single-use (never warmed), unwarmable, /mp harvest.
        var playing = Soak("Channel Playing");
        var warm = Soak("Channel Warm");
        var cookie = CookieFb("Channel Cookie");
        var unwarmable = Soak("Channel Unwarmable");
        var mp = MpHarvest("Channel MP", "http://oak-lane.m3u8");

        // Act
        var first = FirstPlayOpenStrategy.CountBackupsNeedingWarm(
            [playing, warm, cookie], playing, Now,
            row => ReferenceEquals(row, warm) ? (Now + TimeSpan.FromMinutes(10), Now) : null,
            _ => false);
        var second = FirstPlayOpenStrategy.CountBackupsNeedingWarm(
            [playing, unwarmable, mp], playing, Now,
            _ => null,
            row => ReferenceEquals(row, unwarmable));

        // Assert
        Assert.Equal(0, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public void CountBackupsNeedingWarm_OnlyLooksAtPrewarmDepth()
    {
        // Arrange — depth 2: u0, u1 warm; u2 is not looked at.
        var playing = Soak("Channel P");
        var u0 = Soak("Channel U0");
        var u1 = Soak("Channel U1");
        var u2 = Soak("Channel U2");

        // Act
        var n = FirstPlayOpenStrategy.CountBackupsNeedingWarm(
            [playing, u0, u1, u2], playing, Now,
            row => ReferenceEquals(row, u2) ? null : (Now + TimeSpan.FromMinutes(10), Now),
            _ => false);

        // Assert
        Assert.Equal(2, PlaybackUrlReusePolicy.PrewarmDepth);
        Assert.Equal(0, n);
    }

    // --- helpers -------------------------------------------------------------

    // --- Backup order (rule 11: High, then Medium, then finding) ---------------

    [Fact]
    public void UpcomingInBackupOrder_BestTierFirst_ThenChannelNumber()
    {
        // Arrange — #1 plays; #2 and #3 are finding, #4 Medium, #5 High.
        var playing = Soak("Channel 1");
        var f2 = Soak("Channel 2");
        var f3 = Soak("Channel 3");
        var m4 = Soak("Channel 4");
        var h5 = Soak("Channel 5");
        var tiers = new Dictionary<EnrichedStream, StreamTier>(ReferenceEqualityComparer.Instance)
        {
            [playing] = StreamTier.High, [m4] = StreamTier.Medium, [h5] = StreamTier.High,
        };
        StreamTier TierOf(EnrichedStream row) => tiers.TryGetValue(row, out var t) ? t : StreamTier.Finding;

        // Act
        var order = PlaybackUrlReusePolicy.UpcomingInBackupOrder([playing, f2, f3, m4, h5], playing, 5, TierOf);

        // Assert
        Assert.Equal([h5, m4, f2, f3], order);
    }

    [Fact]
    public void UpcomingInBackupOrder_NoTiers_IsSwitchOrder()
    {
        // Arrange
        var a = Soak("Channel A");
        var b = Soak("Channel B");
        var c = Soak("Channel C");

        // Act
        var order = PlaybackUrlReusePolicy.UpcomingInBackupOrder([a, b, c], b, 2, tierOf: null);

        // Assert
        Assert.Equal([c, a], order);
    }

    [Fact]
    public void PlanStart_FreshStartWithTiers_PartnerIsTheBestTierRow()
    {
        // Arrange — single-use start row; the next channel is finding, a later one Medium.
        var start = CookieFb("Channel Start");
        var finding = CookieFb("Channel Finding");
        var medium = CookieFb("Channel Medium");
        StreamTier TierOf(EnrichedStream row) => ReferenceEquals(row, medium) ? StreamTier.Medium : StreamTier.Finding;

        // Act
        var plan = FirstPlayOpenStrategy.PlanStart(start, [start, finding, medium], Now, tierOf: TierOf);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
        Assert.Same(medium, plan.PartnerToWarm);
    }

    [Fact]
    public void CountBackupsNeedingWarm_Upcoming_CountsAtMostPrewarmDepth()
    {
        // Arrange — the prewarmer looks at PrewarmDepth rows of the upcoming list.
        var upcoming = new List<EnrichedStream>();
        for (var i = 0; i < PlaybackUrlReusePolicy.PrewarmDepth + 2; i++)
            upcoming.Add(Soak($"Channel {i}"));

        // Act
        var n = FirstPlayOpenStrategy.CountBackupsNeedingWarm(upcoming, Now, _ => null, _ => false);

        // Assert
        Assert.Equal(PlaybackUrlReusePolicy.PrewarmDepth, n);
    }

    private static EnrichedStream Soak(string name) => new()
    {
        Stream = new Stream { Url = $"https://streams.example.com/{name}", Channel = name },
        Status = StreamResolutionStatus.Healthy,
        Referer = "https://streams.example.com/",
        RequestHeaders = new Dictionary<string, string> { ["Referer"] = "https://streams.example.com/" },
    };

    private static EnrichedStream CookieFb(string name) => new()
    {
        Stream = new Stream { Url = $"https://streams.example.com/fb/{name}", Channel = name },
        Status = StreamResolutionStatus.Healthy,
        Referer = "https://streams.example.com/fb/",
        RequestHeaders = new Dictionary<string, string>
        {
            ["Referer"] = "https://streams.example.com/fb/",
            ["Cookie"] = "session=1",
        },
    };

    private static EnrichedStream MpHarvest(string name, string url) => new()
    {
        Stream = new Stream { Url = $"https://streams.example.com/mp/{name}", Channel = name },
        Status = StreamResolutionStatus.Healthy,
        ResolvedM3U8Url = url,
        RewrittenSegments = ["https://cdn.example.com/seg.exe?_s2=1"],
    };

    private static EnrichedStream Plain(string? url) => new()
    {
        Stream = new Stream { Url = "https://streams.example.com/x", Channel = "Channel X" },
        Status = StreamResolutionStatus.Healthy,
        ResolvedM3U8Url = url,
    };
}
