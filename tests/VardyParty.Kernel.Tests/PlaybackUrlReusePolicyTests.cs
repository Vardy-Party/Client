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
    public void IsKnownSingleUse_CookieRewrittenOrRegisteredHost()
    {
        // Arrange
        PlaybackUrlReusePolicy.RegisterSingleUseHost("burn-once.northgate.test");

        // Assert
        Assert.False(PlaybackUrlReusePolicy.IsKnownSingleUse(Row(M3u8TokenExpiryTests.UzhiUrl, headers: RefererSoak())));
        Assert.True(PlaybackUrlReusePolicy.IsKnownSingleUse(
            Row(M3u8TokenExpiryTests.UzhiUrl, headers: new() { ["Cookie"] = "fb=1", ["Referer"] = "https://p.test/" })));
        Assert.True(PlaybackUrlReusePolicy.IsKnownSingleUse(
            Row(M3u8TokenExpiryTests.UzhiUrl, rewritten: ["https://kdns.fr/seg.png"])));
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
        // Assert
        Assert.True(PlaybackUrlReusePolicy.ShouldPrewarm(Row(null, headers: RefererSoak())));
        Assert.False(PlaybackUrlReusePolicy.ShouldPrewarm(Row("http://oak-lane.m3u8")));
        Assert.False(PlaybackUrlReusePolicy.ShouldPrewarm(Row(null, headers: new() { ["cookie"] = "fb=1" })));
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
        Assert.Equal("Switching to Vivoentv\u2026", PlaybackUrlReusePolicy.SwitchPendingText("Vivoentv"));
        Assert.Equal("Switching to next stream\u2026", PlaybackUrlReusePolicy.SwitchPendingText(" "));
        Assert.Contains("staying on the current stream", PlaybackUrlReusePolicy.SwitchFailedText("Vivoentv"));
    }
}
