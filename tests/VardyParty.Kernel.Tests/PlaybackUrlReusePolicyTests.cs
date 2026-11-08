using System;
using System.Collections.Generic;
using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

public class PlaybackUrlReusePolicyTests
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793137000);

    internal static EnrichedStream Row(
        string? url,
        string channel = "oak-lane-channel",
        Dictionary<string, string>? headers = null,
        List<string>? rewritten = null)
        => new()
        {
            Stream = new Stream { Url = "https://northgate.test/" + channel, Channel = channel },
            Status = StreamResolutionStatus.Healthy,
            ResolvedM3U8Url = url,
            RequestHeaders = headers,
            RewrittenSegments = rewritten,
        };

    internal static Dictionary<string, string> RefererSoak() => new() { ["Referer"] = "https://player.northgate.test/" };

    [Fact]
    public void RequiresFresh_MatchesSoakEvidence()
    {
        // Assert
        Assert.False(PlaybackUrlReusePolicy.RequiresFreshM3u8ForPlayback(null));
        Assert.False(PlaybackUrlReusePolicy.RequiresFreshM3u8ForPlayback(Row("http://oak-lane.m3u8")));
        Assert.True(PlaybackUrlReusePolicy.RequiresFreshM3u8ForPlayback(Row("http://oak-lane.m3u8", headers: RefererSoak())));
        Assert.True(PlaybackUrlReusePolicy.RequiresFreshM3u8ForPlayback(
            Row("http://oak-lane.m3u8", headers: new() { ["cookie"] = "cdn=1" })));
        Assert.True(PlaybackUrlReusePolicy.RequiresFreshM3u8ForPlayback(
            Row("http://oak-lane.m3u8", rewritten: ["https://x.test/seg.ts?_s2=1"])));
        Assert.False(PlaybackUrlReusePolicy.RequiresFreshM3u8ForPlayback(
            Row("http://oak-lane.m3u8", headers: new() { ["referer"] = " " })));
    }

    [Fact]
    public void IsKnownSingleUse_CookieOrRegisteredHost_NotMpHarvest()
    {
        // Arrange
        PlaybackUrlReusePolicy.RegisterSingleUseHost("burn-once.northgate.test");

        // Assert
        Assert.False(PlaybackUrlReusePolicy.IsKnownSingleUse(Row(M3u8TokenExpiryTests.UzhiUrl, headers: RefererSoak())));
        Assert.True(PlaybackUrlReusePolicy.IsKnownSingleUse(
            Row(M3u8TokenExpiryTests.UzhiUrl, headers: new() { ["Cookie"] = "fb=1", ["Referer"] = "https://p.test/" })));
        // /mp harvest streamed the playlist — rewritten segments are probe evidence, not single-use.
        Assert.False(PlaybackUrlReusePolicy.IsKnownSingleUse(
            Row(M3u8TokenExpiryTests.UzhiUrl, rewritten: ["https://cdn.example.com/seg.png?_s2=1"])));
        Assert.True(PlaybackUrlReusePolicy.IsKnownSingleUse(
            Row("https://edge.burn-once.northgate.test/live.m3u8?e=1793147900", headers: RefererSoak())));
    }

    [Fact]
    public void CanReuseCached_NeedsReadableExpiryWithComfortableLife()
    {
        // Assert
        Assert.True(PlaybackUrlReusePolicy.CanReuseCached(Row(M3u8TokenExpiryTests.UzhiUrl, headers: RefererSoak()), Now));
        // cowedd 79s left > 60s
        Assert.True(PlaybackUrlReusePolicy.CanReuseCached(Row(M3u8TokenExpiryTests.CowedUrl, headers: RefererSoak()), Now));
        Assert.False(PlaybackUrlReusePolicy.CanReuseCached(
            Row(M3u8TokenExpiryTests.CowedUrl, headers: RefererSoak()), Now.AddSeconds(20)));
        Assert.False(PlaybackUrlReusePolicy.CanReuseCached(Row("http://oak-lane.m3u8", headers: RefererSoak()), Now));
        Assert.False(PlaybackUrlReusePolicy.CanReuseCached(Row(null, headers: RefererSoak()), Now));
        Assert.False(PlaybackUrlReusePolicy.CanReuseCached(null, Now));
    }

    [Fact]
    public void CanReuseCached_MpHarvestProbeReady_RequiresComfortableLifeOrUnused()
    {
        // Arrange — long-lived harvest may AttachCached; unreadable expiry only on first use.
        var longLived = Row(
            M3u8TokenExpiryTests.UzhiUrl,
            rewritten: ["https://cdn.example.com/seg.exe?_s2=1"]);
        var unreadable = Row(
            "http://oak-lane.m3u8",
            rewritten: ["https://cdn.example.com/seg.exe?_s2=1"]);
        var unreadableUnused = Row(
            "http://oak-lane.m3u8",
            rewritten: ["https://cdn.example.com/seg.png?_s2=1"]);
        unreadableUnused.UnusedResolvedUrl = true;
        var stale = Row(
            M3u8TokenExpiryTests.CowedUrl,
            rewritten: ["https://cdn.example.com/seg.png?_s2=1"]);

        // Act / Assert
        Assert.True(PlaybackUrlReusePolicy.CanReuseCached(longLived, Now));
        var longPlan = PlaybackUrlReusePolicy.PlanSwitch(longLived, Now);
        Assert.Equal(PlaybackSwitchKind.AttachCached, longPlan.Kind);
        Assert.Contains("mp harvest", longPlan.Reason);
        Assert.False(PlaybackUrlReusePolicy.ShouldPrewarm(longLived));

        Assert.False(PlaybackUrlReusePolicy.CanReuseCached(unreadable, Now));
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, PlaybackUrlReusePolicy.PlanSwitch(unreadable, Now).Kind);

        Assert.True(PlaybackUrlReusePolicy.CanReuseCached(unreadableUnused, Now));
        Assert.Equal(PlaybackSwitchKind.AttachCached, PlaybackUrlReusePolicy.PlanSwitch(unreadableUnused, Now).Kind);

        Assert.False(PlaybackUrlReusePolicy.CanReuseCached(stale, Now.AddSeconds(20)));
        var stalePlan = PlaybackUrlReusePolicy.PlanSwitch(stale, Now.AddSeconds(20));
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, stalePlan.Kind);
        Assert.Contains("mp harvest token", stalePlan.Reason);
    }

    [Fact]
    public void CanReuseCached_UnusedPlaybackUrl_SkipsSecondPlay()
    {
        // Arrange
        var justResolved = Row("http://oak-lane.m3u8", headers: new() { ["Cookie"] = "fb=1" });
        justResolved.UnusedResolvedUrl = true;

        // Act
        var canReuse = PlaybackUrlReusePolicy.CanReuseCached(justResolved, Now);
        var plan = PlaybackUrlReusePolicy.PlanSwitch(justResolved, Now);

        // Assert
        Assert.True(canReuse);
        Assert.Equal(PlaybackSwitchKind.AttachCached, plan.Kind);
        Assert.Contains("unused", plan.Reason);
    }

    [Fact]
    public void PlanSwitch_CoversEachKind()
    {
        // Act
        var none = PlaybackUrlReusePolicy.PlanSwitch(null, Now);
        var plain = PlaybackUrlReusePolicy.PlanSwitch(Row("http://oak-lane.m3u8"), Now);
        var plainNoUrl = PlaybackUrlReusePolicy.PlanSwitch(Row(null), Now);
        var reusable = PlaybackUrlReusePolicy.PlanSwitch(Row(M3u8TokenExpiryTests.UzhiUrl, headers: RefererSoak()), Now);
        var unknown = PlaybackUrlReusePolicy.PlanSwitch(Row("http://oak-lane.m3u8", headers: RefererSoak()), Now);
        var cookie = PlaybackUrlReusePolicy.PlanSwitch(
            Row(M3u8TokenExpiryTests.UzhiUrl, headers: new() { ["cookie"] = "fb=1" }), Now);

        // Assert
        Assert.Equal(PlaybackSwitchKind.None, none.Kind);
        Assert.Equal(PlaybackSwitchKind.AttachCached, plain.Kind);
        Assert.Equal("http://oak-lane.m3u8", plain.Url);
        Assert.Equal(PlaybackSwitchKind.None, plainNoUrl.Kind);
        Assert.Equal(PlaybackSwitchKind.AttachCached, reusable.Kind);
        Assert.Equal(M3u8TokenExpiryTests.UzhiUrl, reusable.Url);
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, unknown.Kind);
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, cookie.Kind);
        Assert.Contains("single-use", cookie.Reason);
    }

    [Fact]
    public void ShouldPrewarm_OnlyReusableOneShotRows()
    {
        // Arrange
        var refererSoak = Row(null, headers: RefererSoak());
        var plain = Row("http://oak-lane.m3u8");
        var cookie = Row(null, headers: new() { ["cookie"] = "fb=1" });
        var harvest = Row(
            "http://oak-lane.m3u8",
            rewritten: ["https://cdn.example.com/seg.exe?_s2=1"]);

        // Act / Assert
        Assert.True(PlaybackUrlReusePolicy.ShouldPrewarm(refererSoak));
        Assert.False(PlaybackUrlReusePolicy.ShouldPrewarm(plain));
        Assert.False(PlaybackUrlReusePolicy.ShouldPrewarm(cookie));
        Assert.False(PlaybackUrlReusePolicy.ShouldPrewarm(harvest));
        Assert.False(PlaybackUrlReusePolicy.ShouldPrewarm(null));
    }

    [Theory]
    [InlineData(null, null, 0, true)]
    [InlineData(9000L, 0L, 0, false)]
    [InlineData(80L, -9000L, 0, true)]
    [InlineData(80L, -9000L, 1, true)]
    [InlineData(80L, -40L, 0, true)]
    [InlineData(80L, -40L, 1, false)]
    [InlineData(120L, 0L, 1, false)]
    public void NeedsPrewarmRefresh_Table(long? expiresInSeconds, long? resolvedAgoOffset, int depth, bool expected)
    {
        // Arrange
        DateTimeOffset? expiry = expiresInSeconds is { } e ? Now.AddSeconds(e) : null;
        DateTimeOffset? resolvedAt = resolvedAgoOffset is { } r ? Now.AddSeconds(r) : null;

        // Act
        var needs = PlaybackUrlReusePolicy.NeedsPrewarmRefresh(expiry, resolvedAt, depth, Now);

        // Assert
        Assert.Equal(expected, needs);
    }

    [Fact]
    public void UpcomingInSwitchOrder_WrapsSkipsCurrentAndHonoursDepth()
    {
        // Arrange
        var a = Row("a", "a");
        var b = Row("b", "b");
        var c = Row("c", "c");
        var pool = new List<EnrichedStream> { a, b, c };

        // Assert
        Assert.Equal(new[] { c, a }, PlaybackUrlReusePolicy.UpcomingInSwitchOrder(pool, b, 2));
        Assert.Equal(new[] { b }, PlaybackUrlReusePolicy.UpcomingInSwitchOrder(pool, a, 1));
        Assert.Equal(new[] { b, c }, PlaybackUrlReusePolicy.UpcomingInSwitchOrder(pool, a, 5));
        Assert.Empty(PlaybackUrlReusePolicy.UpcomingInSwitchOrder(new List<EnrichedStream> { a }, a, 2));
        Assert.Equal(new[] { a, b }, PlaybackUrlReusePolicy.UpcomingInSwitchOrder(pool, null, 2));
        Assert.Empty(PlaybackUrlReusePolicy.UpcomingInSwitchOrder(pool, a, 0));
    }

    [Fact]
    public void SwitchTexts_NameTheTarget()
    {
        // Assert
        Assert.Equal("Switching to Channel North\u2026", PlaybackUrlReusePolicy.SwitchPendingText("Channel North"));
        Assert.Equal("Switching to next stream\u2026", PlaybackUrlReusePolicy.SwitchPendingText(" "));
        Assert.Contains("staying on the current stream", PlaybackUrlReusePolicy.SwitchFailedText("Channel North"));
    }
}
