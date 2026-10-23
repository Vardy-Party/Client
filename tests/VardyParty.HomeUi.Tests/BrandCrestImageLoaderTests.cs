using System;
using System.IO;
using System.Linq;
using System.Reflection;
using VardyParty.HomeUi;
using Xunit;

namespace VardyParty.HomeUi.Tests;

public class BrandCrestImageLoaderTests
{
    [Fact]
    public void GetDiscAndMark_LoadEmbeddedPngsAsImageSources()
    {
        // Arrange
        // Act
        var disc = BrandCrestImageLoader.GetDisc();
        var mark = BrandCrestImageLoader.GetMark();

        // Assert — pre-baked PNG embeds; no Svg.Skia natives on the test host.
        Assert.NotNull(disc);
        Assert.NotNull(mark);
    }

    [Fact]
    public void DiscAndMark_AreDistinctEmbeddedPngs()
    {
        // Arrange
        var assembly = typeof(BrandCrestImageLoader).Assembly;

        // Act
        var disc = ReadEmbedded(assembly, "brand_crest_disc.png");
        var mark = ReadEmbedded(assembly, "brand_crest_mark.png");

        // Assert — the spinning mark must not be a second copy of the disc,
        // or the baked highlight and rim would turn with the pattern.
        Assert.NotNull(disc);
        Assert.NotNull(mark);
        Assert.True(disc!.Length > 8);
        Assert.Equal(0x89, disc[0]);
        Assert.Equal((byte)'P', disc[1]);
        Assert.False(disc.SequenceEqual(mark));
    }

    private static byte[]? ReadEmbedded(Assembly assembly, string suffix)
    {
        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name == null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream == null)
        {
            return null;
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
