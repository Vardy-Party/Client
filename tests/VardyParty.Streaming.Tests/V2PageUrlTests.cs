using Xunit;

namespace VardyParty.Streaming.Tests;

public sealed class V2PageUrlTests
{
    [Theory]
    [InlineData("v2", "mp")]
    [InlineData("v2", null)]
    [InlineData(null, "mp")]
    public void IsV2_WhenCatalogSaysV2(string? strategy, string? source) =>
        Assert.True(V2PageUrl.IsV2(strategy, source));

    [Theory]
    [InlineData("v1", "fb")]
    [InlineData("", "fb")]
    [InlineData(null, null)]
    public void IsV2_FalseForV1(string? strategy, string? source) =>
        Assert.False(V2PageUrl.IsV2(strategy, source));

    [Fact]
    public void UseV2Endpoint_RequiresCapabilityAndV2Catalog()
    {
        Assert.True(V2PageUrl.UseV2Endpoint("v2", "mp", ["play.stream", "v2.chrome"]));
        Assert.False(V2PageUrl.UseV2Endpoint("v2", "mp", ["play.stream"]));
        Assert.False(V2PageUrl.UseV2Endpoint("v1", "fb", ["v2.chrome"]));
    }
}
