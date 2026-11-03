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
        var row = Plain("https://cdn.example.com/a.m3u8?e=1893456000");
        var plan = FirstPlayOpenStrategy.PlanStart(row, [row], Now);
        Assert.Equal(FirstPlayOpenStrategy.StartKind.UseExisting, plan.Kind);
        Assert.Null(plan.PartnerToWarm);
        Assert.Contains("without fresh", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanStart_MpHarvestProbeReady_UseExisting_NoPartnerWarm()
    {
        // Real life: /mp already streamed playlist + rewritten segments to the client.
        var mp = MpHarvest("1X EN", "http://oak-lane.m3u8");
        var fb = Soak("Hadcrack");
        var plan = FirstPlayOpenStrategy.PlanStart(mp, [mp, fb], Now);
        Assert.Equal(FirstPlayOpenStrategy.StartKind.UseExisting, plan.Kind);
        Assert.Null(plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_TwoFbHealthy_OpensRecommended_WarmsNextInSwitchOrder()
    {
        // Real life: rSoccerlink + Hadcrack both healthy — open first, warm second for Next.
        var rec = Soak("rSoccerlink");
        var partner = Soak("Hadcrack");
        var plan = FirstPlayOpenStrategy.PlanStart(rec, [rec, partner], Now);
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
        Assert.Same(partner, plan.PartnerToWarm);
        Assert.Contains("Hadcrack", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanStart_ThreeHealthy_PartnerIsImmediateNextOnly()
    {
        var a = Soak("Apexstreams");
        var b = Soak("1ststreams");
        var c = Soak("Liveverse");
        var plan = FirstPlayOpenStrategy.PlanStart(a, [a, b, c], Now);
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
        Assert.Same(b, plan.PartnerToWarm);
        Assert.NotSame(c, plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_NeedsFresh_NoPartner_StillOpenRecommendedFirst()
    {
        var rec = Soak("solo");
        var plan = FirstPlayOpenStrategy.PlanStart(rec, [rec], Now);
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
        Assert.Null(plan.PartnerToWarm);
        Assert.Contains("no partner", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlanStart_RecommendedAlreadyWarmOrInFlight_UseExisting()
    {
        var rec = Soak("north");
        var partner = Soak("south");
        var plan = FirstPlayOpenStrategy.PlanStart(
            rec, [rec, partner], Now, isWarmOrInFlight: r => ReferenceEquals(r, rec));
        Assert.Equal(FirstPlayOpenStrategy.StartKind.UseExisting, plan.Kind);
        Assert.Null(plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_PartnerAlreadyWarm_OpensRecommended_WithoutPartnerWarm()
    {
        var rec = Soak("north");
        var partner = Soak("south");
        var plan = FirstPlayOpenStrategy.PlanStart(
            rec, [rec, partner], Now, isWarmOrInFlight: r => ReferenceEquals(r, partner));
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
        Assert.Null(plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_CachedReusablePartner_NotPickedForWarm()
    {
        var rec = Soak("north");
        var cached = Plain("https://cdn.example.com/east.m3u8?e=1893456000");
        cached.Stream!.Channel = "east";
        var plan = FirstPlayOpenStrategy.PlanStart(rec, [rec, cached], Now);
        Assert.Equal(FirstPlayOpenStrategy.StartKind.ResolveRecommendedOpenFirst, plan.Kind);
        // PickPartner requires NeedsFreshPlay on partner — cached row is skipped.
        Assert.Null(plan.PartnerToWarm);
    }

    [Fact]
    public void PlanStart_NullArgs_Throw()
    {
        var row = Soak("x");
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
    [InlineData(RaceLegState.Infrastructure, FirstPlayOpenStrategy.AfterRecommendedKind.AwaitPartnerOrCached)]
    [InlineData(RaceLegState.Pending, FirstPlayOpenStrategy.AfterRecommendedKind.Fail)]
    public void DecideAfterRecommended_Table(
        RaceLegState state,
        FirstPlayOpenStrategy.AfterRecommendedKind expected)
    {
        Assert.Equal(expected, FirstPlayOpenStrategy.DecideAfterRecommended(state));
    }

    [Theory]
    [InlineData(RaceLegState.Video, true)]
    [InlineData(RaceLegState.Unverified, true)]
    [InlineData(RaceLegState.Empty, false)]
    [InlineData(RaceLegState.AudioOnly, false)]
    [InlineData(RaceLegState.Infrastructure, false)]
    [InlineData(RaceLegState.Pending, false)]
    public void ShouldWarmPartnerLeg_Table(RaceLegState state, bool expected)
        => Assert.Equal(expected, FirstPlayOpenStrategy.ShouldWarmPartnerLeg(state));

    [Theory]
    [InlineData(RaceLegState.Empty, true)]
    [InlineData(RaceLegState.AudioOnly, true)]
    [InlineData(RaceLegState.Video, false)]
    [InlineData(RaceLegState.Unverified, false)]
    [InlineData(RaceLegState.Infrastructure, false)]
    public void IsStreamVerdict_Table(RaceLegState state, bool expected)
        => Assert.Equal(expected, FirstPlayOpenStrategy.IsStreamVerdict(state));

    // --- PlanNextPrepare (early Next / switch) -------------------------------

    [Fact]
    public void PlanNextPrepare_WarmReady_ApplyWarmUnused_EvenWithBurnedFindUrl()
    {
        // Real life: Next onto Hadcrack after partner warm — avoid 15s ResolveFresh.
        var next = Soak("Hadcrack");
        next.ResolvedM3U8Url = "https://find.example/burned.m3u8";
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: true);
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.ApplyWarmUnused, kind);
    }

    [Fact]
    public void PlanNextPrepare_UnusedPlaybackUrl_AttachAsIs()
    {
        var next = Soak("Hadcrack");
        next.ResolvedM3U8Url = "https://cdn.example.com/fresh.m3u8";
        next.UnusedResolvedUrl = true;
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.AttachAsIs, kind);
    }

    [Fact]
    public void PlanNextPrepare_MpHarvestCanReuse_AttachAsIs()
    {
        var next = MpHarvest("TD EN", "http://oak-lane.m3u8");
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.AttachAsIs, kind);
    }

    [Fact]
    public void PlanNextPrepare_FbCookieOneShot_NoWarm_ClearAndResolveFresh()
    {
        var next = CookieFb("Streameast");
        next.ResolvedM3U8Url = "https://find.example/burned.m3u8";
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.ClearAndResolveFresh, kind);
    }

    [Fact]
    public void PlanNextPrepare_RefererSoak_NoWarm_ClearAndResolveFresh()
    {
        var next = Soak("Apexstreams");
        next.ResolvedM3U8Url = "https://find.example/burned.m3u8";
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.ClearAndResolveFresh, kind);
    }

    [Fact]
    public void PlanNextPrepare_EmptyUrl_NonSoak_ClearAndResolveFresh()
    {
        var next = Plain(null);
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.ClearAndResolveFresh, kind);
    }

    [Fact]
    public void PlanNextPrepare_PlainCachedUrl_AttachAsIs()
    {
        var next = Plain("https://cdn.example.com/plain.m3u8");
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: false);
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.AttachAsIs, kind);
    }

    [Fact]
    public void PlanNextPrepare_WarmReadyBeatsBurnedFind_OverClear()
    {
        var next = Soak("x");
        next.ResolvedM3U8Url = "https://burned/";
        Assert.Equal(
            FirstPlayOpenStrategy.NextPrepareKind.ApplyWarmUnused,
            FirstPlayOpenStrategy.PlanNextPrepare(next, Now, warmEntryReady: true));
    }

    [Fact]
    public void PreferImmediateWarmTake_IsAlwaysTrue()
        => Assert.True(FirstPlayOpenStrategy.PreferImmediateWarmTake);

    // --- Playback priority helpers -----------------------------------------

    [Fact]
    public void PlaybackPriorityTimeout_IsThirtySeconds()
        => Assert.Equal(TimeSpan.FromSeconds(30), FirstPlayOpenStrategy.PlaybackPriorityTimeout);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)] // PrewarmDepth is 2
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void PlaybackPrioritySatisfied_Table(int warmCount, bool expected)
    {
        Assert.Equal(2, PlaybackUrlReusePolicy.PrewarmDepth);
        Assert.Equal(expected, FirstPlayOpenStrategy.PlaybackPrioritySatisfied(warmCount));
    }

    [Fact]
    public void CountWarmUpcoming_IgnoresStaleAndMissing()
    {
        var playing = Soak("playing");
        var next1 = Soak("next1");
        var next2 = Soak("next2");
        var n = FirstPlayOpenStrategy.CountWarmUpcoming(
            [playing, next1, next2],
            playing,
            Now,
            row =>
            {
                if (ReferenceEquals(row, next1))
                    return Now + PlaybackUrlReusePolicy.MinComfortableLife + TimeSpan.FromSeconds(10);
                if (ReferenceEquals(row, next2))
                    return Now + TimeSpan.FromSeconds(5); // under MinComfortableLife → ignore
                return null;
            });
        Assert.Equal(1, n);
    }

    [Fact]
    public void CountWarmUpcoming_OnlyLooksAtPrewarmDepth()
    {
        var playing = Soak("p");
        var u0 = Soak("u0");
        var u1 = Soak("u1");
        var u2 = Soak("u2");
        // Depth 2 → u0,u1 only; u2 warm must not count.
        var n = FirstPlayOpenStrategy.CountWarmUpcoming(
            [playing, u0, u1, u2],
            playing,
            Now,
            row => ReferenceEquals(row, u2)
                ? Now + TimeSpan.FromMinutes(5)
                : null);
        Assert.Equal(0, n);
    }

    // --- helpers -------------------------------------------------------------

    private static EnrichedStream Soak(string name) => new()
    {
        Stream = new Stream { Url = $"https://page.example/{name}", Channel = name },
        Status = StreamResolutionStatus.Healthy,
        Referer = "https://page.example/",
        RequestHeaders = new Dictionary<string, string> { ["Referer"] = "https://page.example/" },
    };

    private static EnrichedStream CookieFb(string name) => new()
    {
        Stream = new Stream { Url = $"https://fb.example/{name}", Channel = name },
        Status = StreamResolutionStatus.Healthy,
        Referer = "https://fb.example/",
        RequestHeaders = new Dictionary<string, string>
        {
            ["Referer"] = "https://fb.example/",
            ["Cookie"] = "session=1",
        },
    };

    private static EnrichedStream MpHarvest(string name, string url) => new()
    {
        Stream = new Stream { Url = $"https://mp.example/{name}", Channel = name },
        Status = StreamResolutionStatus.Healthy,
        ResolvedM3U8Url = url,
        RewrittenSegments = ["https://fhlsport.example/seg.exe?_s2=1"],
    };

    private static EnrichedStream Plain(string? url) => new()
    {
        Stream = new Stream { Url = "https://page.example/x", Channel = "x" },
        Status = StreamResolutionStatus.Healthy,
        ResolvedM3U8Url = url,
    };
}
