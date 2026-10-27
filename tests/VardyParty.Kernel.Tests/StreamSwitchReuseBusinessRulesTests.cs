using System;
using System.Collections.Generic;
using VardyParty.Kernel;
using Xunit;
using static VardyParty.Kernel.Tests.PlaybackUrlReusePolicyTests;

namespace VardyParty.Kernel.Tests;

/// <summary>
/// Business rules for V1 switch speed (STREAM_PLAYBACK_RULES.md "Switch speed: reuse,
/// pre-resolve, make-before-break").
/// </summary>
public class StreamSwitchReuseBusinessRulesTests
{
    [Fact]
    public void Rule_V1RowWithLongLivedUzhiToken_SwitchesWithoutPlay()
    {
        // Arrange - Referer soak row whose token still has ~2.5h left.
        var vivoentv = Row(M3u8TokenExpiryTests.UzhiUrl, "Vivoentv", RefererSoak());

        // Act
        var plan = PlaybackUrlReusePolicy.PlanSwitch(vivoentv, Now);

        // Assert
        Assert.Equal(PlaybackSwitchKind.AttachCached, plan.Kind);
    }

    [Fact]
    public void Rule_TokenInsideComfortMargin_RePlays()
    {
        // Arrange - cowedd token with 50s left.
        var totalsportek = Row(M3u8TokenExpiryTests.CowedUrl, "Totalsportek", RefererSoak());

        // Act
        var plan = PlaybackUrlReusePolicy.PlanSwitch(totalsportek, Now.AddSeconds(29));

        // Assert
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, plan.Kind);
    }

    [Fact]
    public void Rule_FbCookieSoakAndMpRewrittenRows_StayOnFreshPath()
    {
        // Arrange - the one-shot case behind 5604455.
        var fb = Row(M3u8TokenExpiryTests.UzhiUrl, "FB", new Dictionary<string, string> { ["Cookie"] = "fb=1" });
        var mp = Row(M3u8TokenExpiryTests.UzhiUrl, "MP", RefererSoak(), ["https://kdns.fr/seg.png"]);

        // Assert
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, PlaybackUrlReusePolicy.PlanSwitch(fb, Now).Kind);
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, PlaybackUrlReusePolicy.PlanSwitch(mp, Now).Kind);
        Assert.False(PlaybackUrlReusePolicy.ShouldPrewarm(fb));
        Assert.False(PlaybackUrlReusePolicy.ShouldPrewarm(mp));
    }

    [Fact]
    public void Rule_UnreadableExpiry_StaysOnFreshPath()
    {
        // Arrange
        var row = Row("https://cdn.northgate.test/live/index.m3u8?sig=abc", "Northgate", RefererSoak());

        // Assert
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, PlaybackUrlReusePolicy.PlanSwitch(row, Now).Kind);
    }

    [Fact]
    public void Rule_PrewarmCoversNextTwo_ShortLivedOnlyImmediateNext()
    {
        // Arrange
        var current = Row("c", "current");
        var next = Row("n", "next");
        var after = Row("a", "after");
        var later = Row("l", "later");
        var pool = new List<EnrichedStream> { current, next, after, later };
        var shortExpiry = Now.AddSeconds(80);
        var shortIssued = Now.AddSeconds(-40);

        // Act
        var upcoming = PlaybackUrlReusePolicy.UpcomingInSwitchOrder(pool, current, PlaybackUrlReusePolicy.PrewarmDepth);

        // Assert
        Assert.Equal(new[] { next, after }, upcoming);
        Assert.True(PlaybackUrlReusePolicy.NeedsPrewarmRefresh(shortExpiry, shortIssued, 0, Now));
        Assert.False(PlaybackUrlReusePolicy.NeedsPrewarmRefresh(shortExpiry, shortIssued, 1, Now));
    }

    [Fact]
    public void Rule_ComfortMarginIsSixtySeconds()
    {
        // Assert
        Assert.Equal(TimeSpan.FromSeconds(60), PlaybackUrlReusePolicy.MinComfortableLife);
        Assert.Equal(2, PlaybackUrlReusePolicy.PrewarmDepth);
    }
}
