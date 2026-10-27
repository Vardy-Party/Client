using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

/// <summary>WebP-EXIF wrapper (castppv / uzhi.ru capture, 27 Oct 2026).</summary>
public class WebpWrappedTsTests
{
    private static byte[] RealHead() => WrappedPngBuilder.Fixture("tiktok-webp-real-head-64k.webp.bin");

    [Fact]
    public void RealCapturedHead_IsWrappedTs()
    {
        var head = RealHead();
        Assert.True(WebpWrappedTs.IsWebp(head));
        Assert.True(WebpWrappedTs.LooksLikeWrappedTs(head));
        Assert.Equal(SegmentPayloadKind.WrappedTs, SegmentPayload.Classify(head));
        Assert.True(SegmentPayload.IsWrapperContainer(head));
    }

    [Fact]
    public void RealCapturedHead_IsTruncated_SoNotUnwrapped_AndNoThrow()
    {
        Assert.False(WebpWrappedTs.TryUnwrap(RealHead(), out var ts, out var failure));
        Assert.Empty(ts);
        Assert.NotEmpty(failure);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(400)]
    [InlineData(401)]
    public void Synthetic_Unwraps_ToExactTs(int packets)
    {
        var ts = WrappedPngBuilder.FakeTs(packets);
        var webp = WrappedPngBuilder.Webp(ts);
        Assert.True(WebpWrappedTs.TryUnwrap(webp, out var got));
        Assert.Equal(ts, got);
        Assert.Equal(ts, SegmentPayload.NormalizeForPlayback(webp, out var unwrapped));
        Assert.True(unwrapped);
    }

    [Fact]
    public void OddSizedChunkBeforeExif_IsSkippedWithPadding()
    {
        var ts = WrappedPngBuilder.FakeTs(20);
        var webp = WrappedPngBuilder.Webp(ts, extraBefore: [("XMP ", new byte[] { 1, 2, 3 })]);
        Assert.True(WebpWrappedTs.TryUnwrap(webp, out var got));
        Assert.Equal(ts, got);
    }

    [Fact]
    public void PlainWebp_IsImage()
    {
        var webp = WrappedPngBuilder.Webp(null);
        Assert.Equal(SegmentPayloadKind.Image, SegmentPayload.Classify(webp));
        Assert.False(SegmentPayload.TryUnwrap(webp, out _));
        Assert.Same(webp, SegmentPayload.NormalizeForPlayback(webp, out var unwrapped));
        Assert.False(unwrapped);
    }

    [Fact]
    public void RealExifMetadata_IsImage()
    {
        var exif = new byte[600];
        "Exif\0\0MM"u8.CopyTo(exif);
        var webp = WrappedPngBuilder.Webp(exif);
        Assert.Equal(SegmentPayloadKind.Image, SegmentPayload.Classify(webp));
        Assert.False(WebpWrappedTs.TryUnwrap(webp, out _));
    }

    [Fact]
    public void SingleSyncByte_IsNotEnough()
    {
        var payload = new byte[188 * 3];
        payload[0] = 0x47; // second packet has no sync
        Assert.Equal(SegmentPayloadKind.Image, SegmentPayload.Classify(WrappedPngBuilder.Webp(payload)));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(5)]
    public void BogusRiffSize_NeverThrows(int riffSize)
    {
        var webp = WrappedPngBuilder.Webp(WrappedPngBuilder.FakeTs(10), riffSizeOverride: riffSize);
        Assert.False(WebpWrappedTs.TryUnwrap(webp, out _));
        _ = SegmentPayload.Classify(webp);
    }

    [Fact]
    public void ChunkSizePastEnd_NeverThrows()
    {
        var webp = WrappedPngBuilder.Webp(WrappedPngBuilder.FakeTs(10));
        // Corrupt EXIF chunk size (after RIFF(12) + VP8L(8+13+1)).
        BitConverter.GetBytes(uint.MaxValue).CopyTo(webp, 34 + 4);
        Assert.False(WebpWrappedTs.TryUnwrap(webp, out _));
        Assert.Equal(SegmentPayloadKind.Image, SegmentPayload.Classify(webp));
    }

    [Fact]
    public void Truncations_NeverThrow()
    {
        var webp = WrappedPngBuilder.Webp(WrappedPngBuilder.FakeTs(30));
        for (var n = 0; n < webp.Length; n += 37)
        {
            var cut = webp.AsSpan(0, n).ToArray();
            _ = SegmentPayload.Classify(cut);
            Assert.False(SegmentPayload.TryUnwrap(cut, out _));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4093)]
    [InlineData(int.MaxValue)]
    public void Reader_UnwrapsChunkedWebp(int chunk)
    {
        var ts = WrappedPngBuilder.FakeTs(500);
        var src = new MemoryStream(WrappedPngBuilder.Webp(ts));
        var read = WrappedSegmentReader.Read((b, o, c) => src.Read(b, o, Math.Min(c, chunk)));
        Assert.True(read.Unwrapped);
        Assert.Equal(ts, read.Body);
    }

    [Fact]
    public async Task ReaderAsync_UnwrapsWebp()
    {
        var ts = WrappedPngBuilder.FakeTs(64);
        var read = await WrappedSegmentReader.ReadAsync(new MemoryStream(WrappedPngBuilder.Webp(ts)));
        Assert.True(read.Unwrapped);
        Assert.Equal(ts, read.Body);
    }
}