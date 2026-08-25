using VardyParty.Models;
using Xunit;

namespace VardyParty.Tests;

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
}
