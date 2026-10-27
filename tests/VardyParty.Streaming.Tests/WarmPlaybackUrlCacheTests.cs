using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public class WarmPlaybackUrlCacheTests
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793137000);

    internal const string UzhiUrl =
        "https://tims-nl-0001.uzhi.ru/main/secure/4b2aae1e00000000000000000000000000000000000000000000000000000000/1793146128/skysportsfootball-uk.m3u8";

    internal static string CoweddUrl(long expiryUnix)
        => $"https://edge.cowedd4855ws.sbs/premium35/index.m3u8?t={expiryUnix}.0123456789abcdef0123456789abcdef";

    internal static EnrichedStream SoakRow(string channel, string? resolved = null)
        => new()
        {
            Stream = new Kernel.Stream { Url = "https://northgate.test/" + channel, Channel = channel },
            Status = StreamResolutionStatus.Healthy,
            ResolvedM3U8Url = resolved ?? $"https://cdn.{channel}.test/find.m3u8",
            RequestHeaders = new Dictionary<string, string> { ["Referer"] = "https://player.northgate.test/" + channel },
        };

    [Fact]
    public void Put_WithoutReadableExpiry_IsRejectedAndMarkedUnwarmable()
    {
        // Arrange
        var cache = new WarmPlaybackUrlCache();
        var row = SoakRow("oak-lane");

        // Act
        var stored = cache.Put(row, new M3U8Response { Url = "http://oak-lane.m3u8" }, Now);

        // Assert
        Assert.False(stored);
        Assert.True(cache.IsUnwarmable(row));
        Assert.Null(cache.Peek(row));
    }

    [Fact]
    public void TryTake_ConsumesOnce()
    {
        // Arrange
        var cache = new WarmPlaybackUrlCache();
        var row = SoakRow("oak-lane");
        cache.Put(row, new M3U8Response { Url = UzhiUrl }, Now);

        // Act
        var first = cache.TryTake(row, Now, out var response);
        var second = cache.TryTake(row, Now, out _);

        // Assert
        Assert.True(first);
        Assert.Equal(UzhiUrl, response.Url);
        Assert.False(second);
    }

    [Fact]
    public void TryTake_InsideComfortMargin_DropsEntry()
    {
        // Arrange
        var cache = new WarmPlaybackUrlCache();
        var row = SoakRow("oak-lane");
        cache.Put(row, new M3U8Response { Url = CoweddUrl(1793137050) }, Now);

        // Act
        var taken = cache.TryTake(row, Now, out _);

        // Assert
        Assert.False(taken);
        Assert.Null(cache.Peek(row));
    }

    [Fact]
    public void Entries_AreKeyedByRowReference()
    {
        // Arrange
        var cache = new WarmPlaybackUrlCache();
        var row = SoakRow("oak-lane");
        var twin = SoakRow("oak-lane");
        cache.Put(row, new M3U8Response { Url = UzhiUrl }, Now);

        // Assert
        Assert.False(cache.TryTake(twin, Now, out _));
        Assert.True(cache.TryTake(row, Now, out _));
    }

    [Fact]
    public async Task WaitForInFlightAsync_JoinsRunningPreResolve()
    {
        // Arrange
        var cache = new WarmPlaybackUrlCache();
        var row = SoakRow("oak-lane");
        Assert.True(cache.BeginInFlight(row));
        Assert.False(cache.BeginInFlight(row));

        // Act
        var wait = cache.WaitForInFlightAsync(row, CancellationToken.None);
        Assert.False(wait.IsCompleted);
        cache.Put(row, new M3U8Response { Url = UzhiUrl }, Now);
        cache.EndInFlight(row);
        await wait;

        // Assert
        Assert.True(cache.TryTake(row, Now, out _));
        await cache.WaitForInFlightAsync(SoakRow("northgate"), CancellationToken.None);
    }

    [Fact]
    public void Prune_DropsRowsLeftOutOfThePool()
    {
        // Arrange
        var cache = new WarmPlaybackUrlCache();
        var kept = SoakRow("oak-lane");
        var gone = SoakRow("northgate");
        cache.Put(kept, new M3U8Response { Url = UzhiUrl }, Now);
        cache.Put(gone, new M3U8Response { Url = UzhiUrl }, Now);
        cache.Put(gone, new M3U8Response { Url = "http://no-expiry.m3u8" }, Now);

        // Act
        cache.Prune(new[] { kept });

        // Assert
        Assert.NotNull(cache.Peek(kept));
        Assert.Null(cache.Peek(gone));
        Assert.False(cache.IsUnwarmable(gone));
    }
}
