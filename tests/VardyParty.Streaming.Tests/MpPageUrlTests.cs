using Xunit;
using VardyParty.Streaming;

namespace VardyParty.Streaming.Tests;

public class MpPageUrlTests
{
    [Theory]
    [InlineData("https://www.fctv-example.test/football/match-1.html")]
    [InlineData("https://jack01.mpgreatest-example.my/football/match.html")]
    [InlineData("https://cdn.mpoutqn4vebroad.example/match.html")]
    [InlineData("https://player.example.test/player.html?mdata=abc")]
    public void IsMpPage_AcceptsMpHosts(string url)
    {
        // Arrange / Act
        var isMp = MpPageUrl.IsMpPage(url);

        // Assert
        Assert.True(isMp);
    }

    [Theory]
    [InlineData("https://www.facebook.com/watch/?v=1")]
    [InlineData("https://streams.example.com/match")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void IsMpPage_RejectsNonMpPages(string url)
    {
        // Arrange / Act
        var isMp = MpPageUrl.IsMpPage(url);

        // Assert
        Assert.False(isMp);
    }

    [Fact]
    public void UseMpEndpoint_RequiresCapabilityAndMpUrl()
    {
        // Arrange
        const string mpUrl = "https://www.fctv-example.test/football/match-1.html";
        const string otherUrl = "https://streams.example.com/match";

        // Act
        var withCapability = MpPageUrl.UseMpEndpoint(mpUrl, ["play.stream", "mp.chrome"]);
        var withoutCapability = MpPageUrl.UseMpEndpoint(mpUrl, ["play.stream"]);
        var nonMp = MpPageUrl.UseMpEndpoint(otherUrl, ["mp.chrome"]);

        // Assert
        Assert.True(withCapability);
        Assert.False(withoutCapability);
        Assert.False(nonMp);
    }
}
