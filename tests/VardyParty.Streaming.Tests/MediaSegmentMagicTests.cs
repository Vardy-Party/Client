using Xunit;
using VardyParty.Streaming;

namespace VardyParty.Streaming.Tests;

public class MediaSegmentMagicTests
{
    [Fact]
    public void LooksLikePlayableMedia_MpegTs()
    {
        Assert.True(MediaSegmentMagic.LooksLikePlayableMedia(new byte[] { 0x47, 0x00, 0x00 }));
    }

    [Fact]
    public void LooksLikePlayableMedia_RejectsJpegEvenWithOctetStreamType()
    {
        Assert.False(MediaSegmentMagic.LooksLikePlayableMedia(
            new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 },
            "application/octet-stream"));
    }

    [Fact]
    public void LooksLikePlayableMedia_RejectsImageContentType()
    {
        Assert.False(MediaSegmentMagic.LooksLikePlayableMedia(
            new byte[] { 0x00, 0x01, 0x02, 0x03 },
            "image/jpeg"));
    }

    [Fact]
    public void LooksLikePlayableMedia_FtypBox()
    {
        // size(4) + 'ftyp'
        var prefix = new byte[] { 0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p' };
        Assert.True(MediaSegmentMagic.LooksLikePlayableMedia(prefix));
    }
}
