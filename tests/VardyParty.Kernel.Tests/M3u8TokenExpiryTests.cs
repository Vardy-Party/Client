using System;
using System.Text;
using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

public class M3u8TokenExpiryTests
{
    internal const string UzhiUrl =
        "https://tims-nl-0001.uzhi.ru/main/secure/4b2aae1e00000000000000000000000000000000000000000000000000000000/1793146128/skysportsfootball-uk.m3u8";

    internal const string CowedUrl =
        "https://edge.cowedd4855ws.sbs/premium35/index.m3u8?t=1793137079.0123456789abcdef0123456789abcdef&_=1793136956867";

    [Fact]
    public void TryGetExpiry_UzhiPathSegment_ReadsUnixSeconds()
    {
        // Act
        var found = M3u8TokenExpiry.TryGetExpiry(UzhiUrl, out var expiry);

        // Assert
        Assert.True(found);
        Assert.Equal(1793146128, expiry.ToUnixTimeSeconds());
    }

    [Fact]
    public void TryGetExpiry_CoweddTQuery_ReadsUnixBeforeSignature()
    {
        // Act
        var found = M3u8TokenExpiry.TryGetExpiry(CowedUrl, out var expiry);

        // Assert
        Assert.True(found);
        Assert.Equal(1793137079, expiry.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData("https://edgestream7.pro/hls/northgate.m3u8?st=abc&e=1793147900", 1793147900)]
    [InlineData("https://cdn.northgate.test/live.m3u8?exp=1793140000", 1793140000)]
    [InlineData("https://cdn.northgate.test/live.m3u8?Expires=1793140000000", 1793140000)]
    public void TryGetExpiry_GenericQueryKeys_ReadSecondsOrMillis(string url, long expected)
    {
        // Act
        var found = M3u8TokenExpiry.TryGetExpiry(url, out var expiry);

        // Assert
        Assert.True(found);
        Assert.Equal(expected, expiry.ToUnixTimeSeconds());
    }

    [Fact]
    public void TryGetExpiry_JwtTokenQuery_ReadsExpClaim()
    {
        // Arrange
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var url = "https://sporty-aoura.test/simpch12/index.m3u8?token="
            + B64("{\"alg\":\"HS256\"}") + "." + B64("{\"sub\":\"oak\",\"exp\":1793138912}") + ".c2ln";

        // Act
        var found = M3u8TokenExpiry.TryGetExpiry(url, out var expiry);

        // Assert
        Assert.True(found);
        Assert.Equal(1793138912, expiry.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://oak-lane.m3u8")]
    [InlineData("http://northgate.test/a.m3u8?t=1")]
    [InlineData("https://live2.totalsportek.today/Fleetwood-Town-vs-Arsenal/78515")]
    [InlineData("https://cdn.northgate.test/9999999999/live.m3u8")]
    [InlineData("https://cdn.northgate.test/live/1793146128.m3u8")]
    public void TryGetExpiry_NoReadableExpiry_ReturnsFalse(string? url)
    {
        // Act
        var found = M3u8TokenExpiry.TryGetExpiry(url, out _);

        // Assert
        Assert.False(found);
    }

    [Fact]
    public void Register_HookForOwnHost_WinsOverBuiltIns()
    {
        // Arrange
        M3u8TokenExpiry.Register(uri => uri.Host == "hook-oak-lane.test"
            ? DateTimeOffset.FromUnixTimeSeconds(1793150000)
            : null);

        // Act
        var found = M3u8TokenExpiry.TryGetExpiry("https://hook-oak-lane.test/x/1793146128/a.m3u8", out var expiry);

        // Assert
        Assert.True(found);
        Assert.Equal(1793150000, expiry.ToUnixTimeSeconds());
    }

    [Fact]
    public void Register_ThrowingHook_IsIgnored()
    {
        // Arrange
        M3u8TokenExpiry.Register(uri => uri.Host == "hook-throw.test"
            ? throw new InvalidOperationException("northgate hook")
            : null);

        // Act
        var found = M3u8TokenExpiry.TryGetExpiry("https://hook-throw.test/live.m3u8?e=1793147900", out var expiry);

        // Assert
        Assert.True(found);
        Assert.Equal(1793147900, expiry.ToUnixTimeSeconds());
    }

    [Fact]
    public void RemainingLife_WhenReadable_IsExpiryMinusNow()
    {
        // Arrange
        var now = DateTimeOffset.FromUnixTimeSeconds(1793137000);

        // Act
        var left = M3u8TokenExpiry.RemainingLife(CowedUrl, now);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(79), left);
        Assert.Null(M3u8TokenExpiry.RemainingLife("http://oak-lane.m3u8", now));
    }
}
