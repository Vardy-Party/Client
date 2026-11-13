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
        var longLived = Row(M3u8TokenExpiryTests.UzhiUrl, "Channel North", RefererSoak());

        // Act
        var plan = PlaybackUrlReusePolicy.PlanSwitch(longLived, Now);

        // Assert
        Assert.Equal(PlaybackSwitchKind.AttachCached, plan.Kind);
    }

    [Fact]
    public void Rule_TokenInsideComfortMargin_RePlays()
    {
        // Arrange - cowedd token with 50s left.
        var shortLived = Row(M3u8TokenExpiryTests.CowedUrl, "Channel East", RefererSoak());

        // Act
        var plan = PlaybackUrlReusePolicy.PlanSwitch(shortLived, Now.AddSeconds(29));

        // Assert
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, plan.Kind);
    }

    [Fact]
    public void Rule_FbCookieStaysFresh_MpHarvestAttachesWithoutPrewarm()
    {
        // Arrange — FB cookie is single-use; MP harvest already streamed playlist to client.
        var fb = Row(M3u8TokenExpiryTests.UzhiUrl, "Channel FB", new Dictionary<string, string> { ["Cookie"] = "fb=1" });
        var mp = Row(M3u8TokenExpiryTests.UzhiUrl, "Channel MP", RefererSoak(), ["https://cdn.example.com/seg.png?_s2=1"]);

        // Act
        var fbPlan = PlaybackUrlReusePolicy.PlanSwitch(fb, Now);
        var mpPlan = PlaybackUrlReusePolicy.PlanSwitch(mp, Now);

        // Assert
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, fbPlan.Kind);
        Assert.Equal(PlaybackSwitchKind.AttachCached, mpPlan.Kind);
        Assert.False(PlaybackUrlReusePolicy.ShouldPrewarm(fb));
        Assert.False(PlaybackUrlReusePolicy.ShouldPrewarm(mp));
    }

    [Fact]
    public void Rule_TwoPartSignedJsonToken_WithLifeLeft_AttachesCached()
    {
        // Arrange - Windows 13 Nov 2026 19:41:13: Next onto LiveStreams went ResolveFresh
        // ("token expiry unknown") and waited 15s for /play behind the find-phase probes,
        // although its token said 29 min were left.
        static string B64(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = Now.AddMinutes(29).ToUnixTimeSeconds();
        var url = "https://sha.sharpnessonline.store/simpch21/index.m3u8?token="
            + B64("{\"stream\":\"simpch21\",\"ip\":\"0.0.0.0\",\"exp\":" + exp + "}") + ".333773ca98f5";
        var row = Row(url, "LiveStreams", RefererSoak());

        // Act
        var plan = PlaybackUrlReusePolicy.PlanSwitch(row, Now);

        // Assert
        Assert.Equal(PlaybackSwitchKind.AttachCached, plan.Kind);
    }

    [Fact]
    public void Rule_UnreadableExpiry_PlaysTheProbedLink_FailedStartReResolvesOnce()
    {
        // Arrange - round 12: V1 switching is immediate like V2 (Jon, 13 Nov 2026).
        var row = Row("https://cdn.northgate.test/live/index.m3u8?sig=abc", "Northgate", RefererSoak());

        // Act
        var plan = PlaybackUrlReusePolicy.PlanSwitch(row, Now);

        // Assert
        Assert.Equal(PlaybackSwitchKind.AttachCached, plan.Kind);
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
