using System.Linq;
using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

public class StreamUrlNormalizerTests
{
    [Theory]
    [InlineData("https://cdn.example.com/live/master.m3u8?token=abc", "https://cdn.example.com/live/master.m3u8")]
    [InlineData("https://cdn.example.com/live/master.m3u8?token=abc#frag", "https://cdn.example.com/live/master.m3u8")]
    [InlineData("https://CDN.example.com/live/master.m3u8/", "https://cdn.example.com/live/master.m3u8")]
    [InlineData("https://cdn.example.com:443/live/master.m3u8", "https://cdn.example.com/live/master.m3u8")]
    public void NormalizeForDedup_StripsTokensAndCanonicalizes(string input, string expected)
    {
        // Arrange
        var url = input;

        // Act
        var normalized = StreamUrlNormalizer.NormalizeForDedup(url);

        // Assert
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void NormalizeForDedup_TreatsDifferentTokensAsSameStream()
    {
        // Arrange
        var firstUrl = "https://cdn.example.com/live/master.m3u8?token=abc123";
        var secondUrl = "https://cdn.example.com/live/master.m3u8?token=xyz789";

        // Act
        var first = StreamUrlNormalizer.NormalizeForDedup(firstUrl);
        var second = StreamUrlNormalizer.NormalizeForDedup(secondUrl);

        // Assert
        Assert.Equal(first, second);
    }

    // Real resolved URLs from the 27 Oct 2026 Fleetwood v Arsenal session.
    private const string Premium38Liveverse =
        "https://edge.cowedd4855ws.sbs/premium38/index.m3u8?t=1793137079.2f8f0bfaab9d30b80ba4ec68055266c1&_=1793136956751";
    private const string Premium38FirstStreams =
        "https://edge.cowedd4855ws.sbs/premium38/index.m3u8?t=1793137099.897440ff42dc2a63570297f7e130584c&_=1793136976705";
    private const string Premium35Totalsportek =
        "https://edge.cowedd4855ws.sbs/premium35/index.m3u8?t=1793137120.0a1b2c3d4e5f60718293a4b5c6d7e8f9&_=1793136999999";
    private const string UzhiSoccermlb =
        "https://tims-nl-0001.uzhi.ru/main/secure/c9db667f6412f888e07a9f1a3c3d18f6e00c6a59d09a253bbd2ae780fc21ff13/1793145999/skysportsfootball-uk.m3u8";
    private const string UzhiAllSportsHd =
        "https://tims-nl-0001.uzhi.ru/main/secure/08b298ba5cd8832d6c55ff57e4bece14a47d87774eb6ced6ac96b9492ac119bc/1793146015/skysportsfootball-uk.m3u8";
    private const string UzhiVivoentv =
        "https://tims-nl-0001.uzhi.ru/main/secure/3eeb169fad82fa1578288d1e8cff40c22a22a8d57f0c9603386c426a4716bcab/1793146067/skysportsfootball-uk.m3u8";

    [Fact]
    public void M3u8IdentityKey_Premium38_FromDifferentSources_IsSame()
    {
        var liveverse = StreamUrlNormalizer.M3u8IdentityKey(Premium38Liveverse);
        var firstStreams = StreamUrlNormalizer.M3u8IdentityKey(Premium38FirstStreams);

        Assert.Equal("https://edge.cowedd4855ws.sbs/premium38/index.m3u8", liveverse);
        Assert.Equal(liveverse, firstStreams);
    }

    [Fact]
    public void M3u8IdentityKey_UzhiTokenAndExpiryPathSegments_AreDropped()
    {
        var keys = new[] { UzhiSoccermlb, UzhiAllSportsHd, UzhiVivoentv }
            .Select(StreamUrlNormalizer.M3u8IdentityKey)
            .Distinct()
            .ToList();

        Assert.Equal(["https://tims-nl-0001.uzhi.ru/main/secure/skysportsfootball-uk.m3u8"], keys);
    }

    [Fact]
    public void M3u8IdentityKey_Premium38AndPremium35_StayDistinct()
    {
        Assert.NotEqual(
            StreamUrlNormalizer.M3u8IdentityKey(Premium38Liveverse),
            StreamUrlNormalizer.M3u8IdentityKey(Premium35Totalsportek));
    }

    [Theory]
    [InlineData(
        "https://tims-nl-0001.uzhi.ru/main/secure/c9db667f6412f888e07a9f1a3c3d18f6e00c6a59d09a253bbd2ae780fc21ff13/1793145999/skysportsfootball-uk.m3u8",
        "https://tims-nl-0001.uzhi.ru/main/secure/c9db667f6412f888e07a9f1a3c3d18f6e00c6a59d09a253bbd2ae780fc21ff13/1793145999/skysportsmain-uk.m3u8")]
    [InlineData(
        "https://tims-nl-0001.uzhi.ru/main/secure/aa/skysportsfootball-uk.m3u8",
        "https://tims-nl-0002.uzhi.ru/main/secure/aa/skysportsfootball-uk.m3u8")]
    [InlineData(
        "https://cdn.example.com/live/one/master.m3u8",
        "https://cdn.example.com/live/two/master.m3u8")]
    [InlineData(
        "https://cdn.example.com/premier-league-hd-channel-number-1/index.m3u8",
        "https://cdn.example.com/premier-league-hd-channel-number-2/index.m3u8")]
    [InlineData(
        "https://cdn.example.com/ch/12345678/index.m3u8",
        "https://cdn.example.com/ch/87654321/index.m3u8")]
    public void M3u8IdentityKey_DifferentChannelOrHost_StayDistinct(string first, string second)
    {
        Assert.NotEqual(StreamUrlNormalizer.M3u8IdentityKey(first), StreamUrlNormalizer.M3u8IdentityKey(second));
    }

    [Theory]
    [InlineData("1793145999", true)]
    [InlineData("1793136956751", true)]
    [InlineData("c9db667f6412f888e07a9f1a3c3d18f6e00c6a59d09a253bbd2ae780fc21ff13", true)]
    [InlineData("2f8f0bfaab9d30b80ba4ec68055266c1", true)]
    [InlineData("Xk3_9QwErTyUiOpAsDfGhJkLzXcVbNm12", true)]
    [InlineData("12345678", false)]
    [InlineData("premium38", false)]
    [InlineData("secure", false)]
    [InlineData("premier-league-hd-channel-number-1", false)]
    [InlineData("abcdef0123456789abcdef012345678", false)]
    [InlineData("", false)]
    public void IsTokenLikeSegment_ClassifiesConservatively(string segment, bool expected)
    {
        Assert.Equal(expected, StreamUrlNormalizer.IsTokenLikeSegment(segment));
    }

    [Fact]
    public void M3u8IdentityKey_FinalFilenameSegment_IsNeverDropped()
    {
        var key = StreamUrlNormalizer.M3u8IdentityKey(
            "https://cdn.example.com/hls/2f8f0bfaab9d30b80ba4ec68055266c1");

        Assert.Equal("https://cdn.example.com/hls/2f8f0bfaab9d30b80ba4ec68055266c1", key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void M3u8IdentityKey_Empty_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, StreamUrlNormalizer.M3u8IdentityKey(input));
    }
}
