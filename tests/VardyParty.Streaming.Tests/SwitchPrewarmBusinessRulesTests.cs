using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Streaming;
using Xunit;
using static VardyParty.Streaming.Tests.WarmPlaybackUrlCacheTests;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// Business rules: one /play per switch at most, and none when the target is warm
/// (STREAM_PLAYBACK_RULES.md "Switch speed: reuse, pre-resolve, make-before-break").
/// </summary>
public class SwitchPrewarmBusinessRulesTests
{
    [Fact]
    public async Task Rule_NextOntoWarmRow_TakesWarmUrlOnce_ThenPrewarmMovesOn()
    {
        // Arrange
        var api = new Mock<IApiService>();
        var calls = new List<string>();
        api.Setup(a => a.GetM3U8UrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, string?, string?, CancellationToken>((url, _, _, _, _) => calls.Add(url))
            .ReturnsAsync(new M3U8Response { Url = UzhiUrl });
        var pool = new StreamSwitchingService();
        pool.Initialize("northgate-league", "Oak Lane", "Northgate");
        var soccer = SoakRow("soccermlbstream");
        var allSports = SoakRow("all-sports-hd");
        var vivoentv = SoakRow("vivoentv");
        pool.AddHealthyStream(soccer);
        pool.AddHealthyStream(allSports);
        pool.AddHealthyStream(vivoentv);
        var cache = new WarmPlaybackUrlCache();
        var time = new UpcomingStreamPrewarmerTests.ManualTime(Now);
        var prewarmer = new UpcomingStreamPrewarmer(api.Object, pool, cache, NullLogger<UpcomingStreamPrewarmer>.Instance, time);
        await prewarmer.TickAsync(CancellationToken.None);

        // Act - Next: the switch consumes the warm URL instead of a fresh /play.
        pool.SwitchToNextStream();
        var took = cache.TryTake(allSports, Now, out var warm);
        await prewarmer.TickAsync(CancellationToken.None);

        // Assert - one /play for All-Sports-HD (warm), then one for the row after it.
        Assert.True(took);
        Assert.Equal(UzhiUrl, warm.Url);
        Assert.Equal(new[] { allSports.Stream.Url, vivoentv.Stream.Url }, calls);
        Assert.DoesNotContain(soccer.Stream.Url, calls);
    }
}
