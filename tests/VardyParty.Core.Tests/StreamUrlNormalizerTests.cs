using VardyParty.Resolvers;
using Xunit;

namespace VardyParty.Core.Tests;

public class StreamUrlNormalizerTests
{
    [Theory]
    [InlineData("https://cdn.example.com/live/master.m3u8?token=abc", "https://cdn.example.com/live/master.m3u8")]
    [InlineData("https://cdn.example.com/live/master.m3u8?token=abc#frag", "https://cdn.example.com/live/master.m3u8")]
    [InlineData("https://CDN.example.com/live/master.m3u8/", "https://cdn.example.com/live/master.m3u8")]
    [InlineData("https://cdn.example.com:443/live/master.m3u8", "https://cdn.example.com/live/master.m3u8")]
    public void NormalizeForDedup_StripsTokensAndCanonicalizes(string input, string expected)
    {
        Assert.Equal(expected, StreamUrlNormalizer.NormalizeForDedup(input));
    }

    [Fact]
    public void NormalizeForDedup_TreatsDifferentTokensAsSameStream()
    {
        var first = StreamUrlNormalizer.NormalizeForDedup("https://cdn.example.com/live/master.m3u8?token=abc123");
        var second = StreamUrlNormalizer.NormalizeForDedup("https://cdn.example.com/live/master.m3u8?token=xyz789");

        Assert.Equal(first, second);
    }
}
