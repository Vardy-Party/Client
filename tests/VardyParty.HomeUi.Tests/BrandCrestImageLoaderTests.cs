using VardyParty.HomeUi;
using Xunit;

namespace VardyParty.HomeUi.Tests;

public class BrandCrestImageLoaderTests
{
    [Fact]
    public void GetCrest_LoadsEmbeddedPngAsImageSource()
    {
        // Pre-baked PNG embed — no Svg.Skia natives required on the test host.
        var source = BrandCrestImageLoader.GetCrest();
        Assert.NotNull(source);
    }
}
