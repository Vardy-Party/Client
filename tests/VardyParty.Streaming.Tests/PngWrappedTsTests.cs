using System;
using System.IO;
using System.Linq;
using Xunit;

namespace VardyParty.Streaming.Tests;

public class PngWrappedTsTests
{
    private const string RealHead = "tiktikpx-real-head-64k.png.bin";
    private const string RealTs = "tiktikpx-real-40pkt-mpegts.bin";

    [Fact]
    public void RealCapturedSegmentHead_IsWrappedTs()
    {
        // First 64 KB of a real cowedd premium1 segment from tiktokcdn-us.com (27 Oct 2026).
        var head = WrappedPngBuilder.Fixture(RealHead);

        Assert.True(PngWrappedTs.IsPng(head));
        Assert.True(PngWrappedTs.HasMarker(head));
        Assert.True(PngWrappedTs.LooksLikeWrappedTs(head));
        Assert.Equal(SegmentPayloadKind.WrappedTs, SegmentPayload.Classify(head));
    }

    [Fact]
    public void RealCapturedSegmentHead_FullUnwrap_FailsSafelyBecauseTruncated()
    {
        var head = WrappedPngBuilder.Fixture(RealHead);

        Assert.False(PngWrappedTs.TryUnwrap(head, out var ts, out var failure));
        Assert.Empty(ts);
        Assert.False(string.IsNullOrEmpty(failure));
    }

    [Fact]
    public void RealTsPackets_WrappedLikeCapture_RoundTripExactly()
    {
        // Real MPEG-TS packets decoded from the capture, re-wrapped with the observed
        // layout: 512 px RGB8, all five PNG filters, stored gzip, padded last row.
        var ts = WrappedPngBuilder.Fixture(RealTs);
        var png = WrappedPngBuilder.Wrap(ts);

        Assert.True(PngWrappedTs.TryUnwrap(png, out var unwrapped, out var failure), failure);
        Assert.Equal(ts, unwrapped);
        Assert.Equal(SegmentPayloadKind.WrappedTs, SegmentPayload.Classify(png));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    [InlineData((byte)6)]
    public void ColourTypes_AllUnwrap(byte colorType)
    {
        var ts = WrappedPngBuilder.FakeTs(60);
        var png = WrappedPngBuilder.Wrap(ts, width: 97, colorType: colorType);

        Assert.True(PngWrappedTs.TryUnwrap(png, out var unwrapped, out var failure), failure);
        Assert.Equal(ts, unwrapped);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public void EachPngFilterType_Unwraps(byte filter)
    {
        var ts = WrappedPngBuilder.FakeTs(30);
        var png = WrappedPngBuilder.Wrap(ts, width: 64, filterForRow: _ => filter);

        Assert.True(PngWrappedTs.TryUnwrap(png, out var unwrapped, out var failure), failure);
        Assert.Equal(ts, unwrapped);
    }

    [Fact]
    public void RawTsPayload_WithoutGzip_Unwraps()
    {
        var ts = WrappedPngBuilder.FakeTs(20);
        var png = WrappedPngBuilder.Png(WrappedPngBuilder.WrapperPixels(ts), width: 128);

        Assert.True(PngWrappedTs.TryUnwrap(png, out var unwrapped, out var failure), failure);
        Assert.Equal(ts, unwrapped);
    }

    [Fact]
    public void PlainImage_WithoutMarker_IsRejected()
    {
        var pixels = new byte[64 * 64 * 3];
        new Random(3).NextBytes(pixels);
        var png = WrappedPngBuilder.Png(pixels, width: 64);

        Assert.False(PngWrappedTs.TryUnwrap(png, out _, out var failure));
        Assert.Contains("marker", failure, StringComparison.OrdinalIgnoreCase);
        Assert.False(PngWrappedTs.HasMarker(png));
        Assert.Equal(SegmentPayloadKind.Image, SegmentPayload.Classify(png));
    }

    [Fact]
    public void WrongMarker_IsRejected()
    {
        var ts = WrappedPngBuilder.FakeTs(10);
        var png = WrappedPngBuilder.Png(
            WrappedPngBuilder.WrapperPixels(WrappedPngBuilder.Gzip(ts), marker: "TIKTIKPY"), width: 64);

        Assert.False(PngWrappedTs.TryUnwrap(png, out _));
        Assert.Equal(SegmentPayloadKind.Image, SegmentPayload.Classify(png));
    }

    [Fact]
    public void MarkerWithNonTsPayload_IsRejected()
    {
        var notTs = System.Text.Encoding.UTF8.GetBytes(new string('x', 2000));
        var png = WrappedPngBuilder.Png(WrappedPngBuilder.WrapperPixels(WrappedPngBuilder.Gzip(notTs)), width: 64);

        Assert.True(PngWrappedTs.HasMarker(png));
        Assert.False(PngWrappedTs.TryUnwrap(png, out _, out var failure));
        Assert.Contains("MPEG-TS", failure);
        Assert.False(SegmentPayload.IsPlayable(SegmentPayload.Classify(png)));
    }

    [Fact]
    public void DeclaredLengthBeyondPixels_IsRejected()
    {
        var ts = WrappedPngBuilder.FakeTs(10);
        var png = WrappedPngBuilder.Png(
            WrappedPngBuilder.WrapperPixels(WrappedPngBuilder.Gzip(ts), padding: 0, declaredLength: 10_000_000), width: 64);

        Assert.False(PngWrappedTs.TryUnwrap(png, out _));
    }

    [Fact]
    public void UnsupportedLayouts_AreRejected()
    {
        var pixels = WrappedPngBuilder.WrapperPixels(WrappedPngBuilder.Gzip(WrappedPngBuilder.FakeTs(10)));
        Assert.False(PngWrappedTs.TryUnwrap(WrappedPngBuilder.Png(pixels, width: 64, depth: 16), out _));
        Assert.False(PngWrappedTs.TryUnwrap(WrappedPngBuilder.Png(pixels, width: 64, interlace: 1), out _));
        Assert.False(PngWrappedTs.TryUnwrap(WrappedPngBuilder.Png(pixels, width: 64, includeEnd: false), out _));
    }

    [Fact]
    public void HugeDeclaredDimensions_AreRejectedWithoutAllocating()
    {
        var png = WrappedPngBuilder.Wrap(WrappedPngBuilder.FakeTs(5), width: 64);
        // IHDR width/height live at bytes 16..23.
        png[16] = 0x7F; png[17] = 0xFF; png[18] = 0xFF; png[19] = 0xFF;
        png[20] = 0x7F; png[21] = 0xFF; png[22] = 0xFF; png[23] = 0xFF;

        Assert.False(PngWrappedTs.TryUnwrap(png, out _, out var failure));
        Assert.Contains("large", failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(33)]
    [InlineData(200)]
    public void TruncatedInput_NeverThrows(int length)
    {
        var png = WrappedPngBuilder.Wrap(WrappedPngBuilder.FakeTs(40), width: 64);
        var cut = png.AsSpan(0, Math.Min(length, png.Length)).ToArray();

        Assert.False(PngWrappedTs.TryUnwrap(cut, out _));
        _ = SegmentPayload.Classify(cut);
        _ = PngWrappedTs.LooksLikeWrappedTs(cut);
    }

    [Fact]
    public void CorruptBytes_Fuzz_NeverThrows()
    {
        var png = WrappedPngBuilder.Wrap(WrappedPngBuilder.FakeTs(40), width: 64);
        var rng = new Random(1234);
        for (var i = 0; i < 400; i++)
        {
            var mutated = (byte[])png.Clone();
            var flips = rng.Next(1, 12);
            for (var f = 0; f < flips; f++)
                mutated[rng.Next(8, mutated.Length)] = (byte)rng.Next(256);
            if (rng.Next(4) == 0)
                mutated = mutated[..rng.Next(8, mutated.Length)];

            var ok = PngWrappedTs.TryUnwrap(mutated, out var ts, out _);
            if (ok)
                Assert.Equal(0x47, ts[0]);
            _ = SegmentPayload.Classify(mutated);
            _ = SegmentPayload.NormalizeForPlayback(mutated, out _);
        }
    }

    [Fact]
    public void NormalizeForPlayback_UnwrapsWrapped_AndPassesEverythingElseThrough()
    {
        var ts = WrappedPngBuilder.FakeTs(30);
        var wrapped = WrappedPngBuilder.Wrap(ts, width: 64);
        var image = WrappedPngBuilder.Png(new byte[300], width: 10);

        Assert.Equal(ts, SegmentPayload.NormalizeForPlayback(wrapped, out var u1));
        Assert.True(u1);
        Assert.Same(ts, SegmentPayload.NormalizeForPlayback(ts, out var u2));
        Assert.False(u2);
        Assert.Same(image, SegmentPayload.NormalizeForPlayback(image, out var u3));
        Assert.False(u3);
    }
}

public class SegmentPayloadTests
{
    [Fact]
    public void MpegTs_IsPlayable()
    {
        var ts = WrappedPngBuilder.FakeTs(4);
        Assert.Equal(SegmentPayloadKind.MpegTs, SegmentPayload.Classify(ts));
        Assert.True(SegmentPayload.IsPlayable(SegmentPayloadKind.MpegTs));
    }

    [Fact]
    public void BrokenTsSync_IsNotMpegTs()
    {
        var ts = WrappedPngBuilder.FakeTs(4);
        ts[188] = 0x00;
        Assert.NotEqual(SegmentPayloadKind.MpegTs, SegmentPayload.Classify(ts));
    }

    [Theory]
    [InlineData("moof")]
    [InlineData("styp")]
    [InlineData("ftyp")]
    public void Fmp4_IsPlayable(string box)
    {
        var body = new byte[64];
        body[3] = 0x18;
        System.Text.Encoding.ASCII.GetBytes(box).CopyTo(body, 4);
        Assert.Equal(SegmentPayloadKind.Fmp4, SegmentPayload.Classify(body));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0, 0 })]
    public void OtherImages_AreNotPlayable(byte[] body)
    {
        Assert.Equal(SegmentPayloadKind.Image, SegmentPayload.Classify(body));
        Assert.False(SegmentPayload.IsPlayable(SegmentPayloadKind.Image));
    }

    [Theory]
    [InlineData("<html><body>403</body></html>")]
    [InlineData("{\"error\":\"denied\"}")]
    public void TextBodies_AreNotPlayable(string text)
    {
        var kind = SegmentPayload.Classify(System.Text.Encoding.UTF8.GetBytes(text));
        Assert.Equal(SegmentPayloadKind.Text, kind);
        Assert.False(SegmentPayload.IsPlayable(kind));
    }

    [Fact]
    public void TinyBody_IsUnknown() =>
        Assert.Equal(SegmentPayloadKind.Unknown, SegmentPayload.Classify([0x47, 0x00]));
}