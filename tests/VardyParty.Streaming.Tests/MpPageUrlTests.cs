using Xunit;

namespace VardyParty.Streaming.Tests;

public sealed class MpPageUrlTests
{
    [Theory]
    [InlineData("v2", "mp")]
    [InlineData("v2", null)]
    [InlineData(null, "mp")]
    public void IsV2_WhenCatalogSaysV2(string? strategy, string? source) =>
        Assert.True(MpPageUrl.IsV2(strategy, source));

    [Theory]
    [InlineData("v1", "fb")]
    [InlineData("", "fb")]
    [InlineData(null, null)]
    public void IsV2_FalseForV1(string? strategy, string? source) =>
        Assert.False(MpPageUrl.IsV2(strategy, source));

    [Fact]
    public void UseMpEndpoint_RequiresCapabilityAndV2Catalog()
    {
        Assert.True(MpPageUrl.UseMpEndpoint("v2", "mp", ["play.stream", "mp.chrome"]));
        Assert.False(MpPageUrl.UseMpEndpoint("v2", "mp", ["play.stream"]));
        Assert.False(MpPageUrl.UseMpEndpoint("v1", "fb", ["mp.chrome"]));
    }
}
