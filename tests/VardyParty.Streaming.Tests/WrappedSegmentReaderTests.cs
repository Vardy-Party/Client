using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public class WrappedSegmentReaderTests
{
    private static Func<byte[], int, int, int> Reader(byte[] source, int maxChunk = int.MaxValue)
    {
        var position = 0;
        return (buffer, offset, count) =>
        {
            var n = Math.Min(Math.Min(count, maxChunk), source.Length - position);
            if (n <= 0)
                return 0;
            Array.Copy(source, position, buffer, offset, n);
            position += n;
            return n;
        };
    }

    [Fact]
    public void PlainTs_OnlyPeeksAndLeavesTheRestUpstream()
    {
        var ts = WrappedPngBuilder.FakeTs(100);
        var read = WrappedSegmentReader.Read(Reader(ts));

        Assert.False(read.Unwrapped);
        Assert.False(read.UpstreamExhausted);
        Assert.Equal(ts.Take(WrappedSegmentReader.PeekBytes), read.Body);
    }

    [Fact]
    public void TinyBody_IsReplayedAndMarkedExhausted()
    {
        var read = WrappedSegmentReader.Read(Reader([0x23, 0x45]));

        Assert.False(read.Unwrapped);
        Assert.True(read.UpstreamExhausted);
        Assert.Equal(new byte[] { 0x23, 0x45 }, read.Body);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    [InlineData(4093)]
    public void WrappedPng_IsUnwrappedWhateverTheUpstreamChunking(int chunk)
    {
        var ts = WrappedPngBuilder.FakeTs(1200);
        var png = WrappedPngBuilder.Wrap(ts);

        var read = WrappedSegmentReader.Read(Reader(png, chunk));

        Assert.True(read.Unwrapped);
        Assert.True(read.UpstreamExhausted);
        Assert.Equal(ts, read.Body);
    }

    [Fact]
    public void PlainPng_IsReturnedUnchanged()
    {
        var png = WrappedPngBuilder.Png(Enumerable.Repeat((byte)0x80, 64 * 64 * 3).ToArray(), 64, 2);

        var read = WrappedSegmentReader.Read(Reader(png));

        Assert.False(read.Unwrapped);
        Assert.True(read.UpstreamExhausted);
        Assert.Equal(png, read.Body);
    }

    [Fact]
    public async Task Async_MatchesSync()
    {
        var ts = WrappedPngBuilder.FakeTs(300, seed: 3);
        var png = WrappedPngBuilder.Wrap(ts);

        var read = await WrappedSegmentReader.ReadAsync(new MemoryStream(png));
        var passthrough = await WrappedSegmentReader.ReadAsync(new MemoryStream(ts));

        Assert.True(read.Unwrapped);
        Assert.Equal(ts, read.Body);
        Assert.False(passthrough.Unwrapped);
        Assert.Equal(ts.Take(WrappedSegmentReader.PeekBytes), passthrough.Body);
    }

    [Fact]
    public void UpstreamFailure_Propagates()
    {
        Assert.Throws<IOException>(() =>
            WrappedSegmentReader.Read((_, _, _) => throw new IOException("HTTP 403")));
    }
}