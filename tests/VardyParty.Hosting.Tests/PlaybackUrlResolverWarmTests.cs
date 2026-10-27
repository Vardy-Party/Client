using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VardyParty.Hosting;
using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Hosting.Tests;

public class PlaybackUrlResolverWarmTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793137000);

    private const string WarmUrl =
        "https://tims-nl-0001.uzhi.ru/main/secure/4b2aae1e00000000000000000000000000000000000000000000000000000000/1793146128/vivoentv.m3u8";

    private readonly Mock<IApiService> _api = new();
    private readonly WarmPlaybackUrlCache _cache = new();
    private readonly FixedTime _time = new(Now);

    private static EnrichedStream Row() => new()
    {
        Stream = new StreamModel { Url = "https://cabgrass.cfd/vivoentv-013/30.php", Channel = "Vivoentv", ResolutionStrategy = "v1" },
        Status = StreamResolutionStatus.Healthy,
        ResolvedM3U8Url = "https://old.example.test/stale.m3u8",
        RequestHeaders = new Dictionary<string, string> { ["Referer"] = "https://player.old.test/" },
    };

    private static M3U8Response Warm() => new()
    {
        Url = WarmUrl,
        RequestHeaders = new Dictionary<string, string> { ["Referer"] = "https://player.vivoentv.test/" },
    };

    private void ApiReturns(string url)
        => _api.Setup(a => a.GetM3U8UrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new M3U8Response { Url = url });

    [Fact]
    public async Task Bind_WarmHit_ReturnsWarmUrlWithoutPlay()
    {
        // Arrange
        var row = Row();
        _cache.Put(row, Warm(), Now.AddSeconds(-20));
        var resolve = PlaybackUrlResolver.Bind(_api.Object, _cache, _time);

        // Act
        var url = await resolve(row, CancellationToken.None);

        // Assert
        Assert.Equal(WarmUrl, url);
        Assert.Equal(WarmUrl, row.ResolvedM3U8Url);
        Assert.Equal("https://player.vivoentv.test/", row.RequestHeaders!["Referer"]);
        _api.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Bind_WarmConsumed_SecondResolveCallsPlay()
    {
        // Arrange
        var row = Row();
        _cache.Put(row, Warm(), Now);
        ApiReturns("https://fresh.example.test/b.m3u8");
        var resolve = PlaybackUrlResolver.Bind(_api.Object, _cache, _time);

        // Act
        await resolve(row, CancellationToken.None);
        var second = await resolve(row, CancellationToken.None);

        // Assert
        Assert.Equal("https://fresh.example.test/b.m3u8", second);
        _api.Verify(a => a.GetM3U8UrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Bind_WarmNearExpiry_FallsBackToFreshPlay()
    {
        // Arrange
        var row = Row();
        _cache.Put(row, new M3U8Response { Url = "https://edge.cowedd.test/p/index.m3u8?t=1793137040.0123456789abcdef" }, Now);
        ApiReturns("https://fresh.example.test/c.m3u8");
        var resolve = PlaybackUrlResolver.Bind(_api.Object, _cache, _time);

        // Act
        var url = await resolve(row, CancellationToken.None);

        // Assert
        Assert.Equal("https://fresh.example.test/c.m3u8", url);
    }

    [Fact]
    public async Task Bind_PrewarmInFlight_JoinsItInsteadOfSecondPlay()
    {
        // Arrange
        var row = Row();
        _cache.BeginInFlight(row);
        var resolve = PlaybackUrlResolver.Bind(_api.Object, _cache, _time);

        // Act
        var pending = resolve(row, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        _cache.Put(row, Warm(), Now);
        _cache.EndInFlight(row);
        var url = await pending;

        // Assert
        Assert.Equal(WarmUrl, url);
        _api.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Bind_WithoutCache_BehavesAsBefore()
    {
        // Arrange
        var row = Row();
        ApiReturns("https://fresh.example.test/d.m3u8");
        var resolve = PlaybackUrlResolver.Bind(_api.Object);

        // Act
        var url = await resolve(row, CancellationToken.None);

        // Assert
        Assert.Equal("https://fresh.example.test/d.m3u8", url);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
