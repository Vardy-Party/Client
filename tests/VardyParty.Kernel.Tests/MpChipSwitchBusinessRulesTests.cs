using System;
using System.Collections.Generic;
using VardyParty.Kernel;
using Xunit;
using static VardyParty.Kernel.Tests.PlaybackUrlReusePolicyTests;

namespace VardyParty.Kernel.Tests;

/// <summary>
/// Business rules for switching onto an MP (V2) chip (Jon, 13 Nov 2026: "i am used to v2
/// switching being immediate"). TV log 18:40:50: Next onto Fubo US, a chip probed Healthy,
/// took ResolveFresh ("token expiry unknown or too close"), the fresh /mp failed and the
/// player stayed on VUS. A chip with a probed link plays that link at once; a fresh
/// resolve is only the fallback after that start fails.
/// </summary>
public class MpChipSwitchBusinessRulesTests
{
    // fhlsport-style token: no readable expiry.
    private const string ProbedChipUrl = "https://fhlsport955.cdx904gouh.xyz/token-LU4qnrSUfa/cfall/s2001/v3b/4qqp/super/3x_942237.m3u8";

    internal static EnrichedStream MpChip(string chip, string? url = ProbedChipUrl, Dictionary<string, string>? headers = null) => new()
    {
        Stream = new Stream
        {
            Url = "https://www.fctv33go.autos/football/moldova-vs-kazakhstan.html",
            Channel = chip,
            PlayerStream = chip,
            Source = "mp",
            ResolutionStrategy = "v2",
        },
        Status = StreamResolutionStatus.Healthy,
        ResolvedM3U8Url = url,
        RequestHeaders = headers ?? new Dictionary<string, string> { ["Referer"] = "https://vida01eo.example.sbs/" },
        UnusedResolvedUrl = false,
    };

    [Fact]
    public void Rule_NextOntoProbedMpChip_UnknownExpiry_PlaysProbedLinkAtOnce()
    {
        // Arrange - the TV log's Fubo US row: probed Healthy, Referer soak, unreadable token.
        var fubo = MpChip("Fubo US");

        // Act
        var plan = PlaybackUrlReusePolicy.PlanSwitch(fubo, Now);

        // Assert
        Assert.Equal(PlaybackSwitchKind.AttachCached, plan.Kind);
        Assert.Equal(ProbedChipUrl, plan.Url);
    }

    [Fact]
    public void Rule_NextPrepare_KeepsTheProbedChipLink_NeverClearsItForAFreshResolve()
    {
        // Arrange
        var fubo = MpChip("Fubo US");

        // Act
        var kind = FirstPlayOpenStrategy.PlanNextPrepare(fubo, Now, warmEntryReady: false);

        // Assert
        Assert.Equal(FirstPlayOpenStrategy.NextPrepareKind.AttachAsIs, kind);
        Assert.Equal(ProbedChipUrl, fubo.ResolvedM3U8Url);
    }

    [Fact]
    public void Rule_ProbedMpChip_IsNotPrewarmed()
    {
        // Arrange - a pre-resolve would start another POST /mp beside the page's harvest.
        var fubo = MpChip("Fubo US");

        // Act
        var prewarm = PlaybackUrlReusePolicy.ShouldPrewarm(fubo);

        // Assert
        Assert.False(prewarm);
    }

    [Fact]
    public void Rule_MpChipWithoutALink_StillResolvesFresh()
    {
        // Arrange
        var chip = MpChip("NW IT", url: null);

        // Act
        var plan = PlaybackUrlReusePolicy.PlanSwitch(chip, Now);

        // Assert
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, plan.Kind);
    }

    [Fact]
    public void Rule_MpChipWithAKnownSingleUseLink_StillResolvesFresh()
    {
        // Arrange - a cookie-bound session cannot be reused.
        var chip = MpChip("TD Arabic", headers: new Dictionary<string, string> { ["Referer"] = "https://p.example/", ["Cookie"] = "s=1" });

        // Act
        var plan = PlaybackUrlReusePolicy.PlanSwitch(chip, Now);

        // Assert
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, plan.Kind);
    }

    [Fact]
    public void Rule_MpChipWhoseReadableTokenIsNearlyExpired_StillResolvesFresh()
    {
        // Arrange - cowedd-style token with 50s left: a readable expiry decides.
        var chip = MpChip("Sport PT", url: M3u8TokenExpiryTests.CowedUrl);

        // Act
        var plan = PlaybackUrlReusePolicy.PlanSwitch(chip, Now.AddSeconds(29));

        // Assert
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, plan.Kind);
    }

    [Fact]
    public void Rule_FbRowWithUnknownExpiry_PlaysItsProbedLinkAtOnce_LikeV2()
    {
        // Arrange - Jon, 13 Nov 2026: "there is a delay switching to v1". A V1 row's probed
        // link plays now; a burned soak token fails its start fast and re-resolves once.
        var fb = Row("https://cdn.northgate.test/live/index.m3u8?sig=abc", "Northgate", RefererSoak());

        // Act
        var plan = PlaybackUrlReusePolicy.PlanSwitch(fb, Now);

        // Assert
        Assert.Equal(PlaybackSwitchKind.AttachCached, plan.Kind);
        Assert.Equal(fb.ResolvedM3U8Url, plan.Url);
        Assert.StartsWith("probed link", plan.Reason);
        Assert.True(PlaybackUrlReusePolicy.CanTryProbedLink(fb, Now));
    }

    [Fact]
    public void Rule_FbCookieRow_OrSingleUseHost_OrNoLink_StaysFresh()
    {
        // Arrange - the exceptions Jon kept: cookie-bound session, single-use host, no link.
        var cookie = Row("https://cdn.northgate.test/live/index.m3u8?sig=abc", "Northgate",
            new Dictionary<string, string> { ["Cookie"] = "fb=1" });
        var noLink = Row(null, "Northgate", RefererSoak());

        // Act
        var cookiePlan = PlaybackUrlReusePolicy.PlanSwitch(cookie, Now);
        var noLinkPlan = PlaybackUrlReusePolicy.PlanSwitch(noLink, Now);

        // Assert
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, cookiePlan.Kind);
        Assert.Equal(PlaybackSwitchKind.ResolveFresh, noLinkPlan.Kind);
        Assert.False(PlaybackUrlReusePolicy.CanTryProbedLink(cookie, Now));
        Assert.False(PlaybackUrlReusePolicy.CanTryProbedLink(noLink, Now));
    }
}
