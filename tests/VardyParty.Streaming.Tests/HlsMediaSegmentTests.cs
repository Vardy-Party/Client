using Xunit;
using VardyParty.Streaming;

namespace VardyParty.Streaming.Tests;

public class HlsMediaSegmentTests
{
    [Theory]
    [InlineData("http://cdn.example.com/seg.ts")]
    [InlineData("http://cdn.example.com/seg.m4s")]
    [InlineData("http://cdn.example.com/chunk")]
    [InlineData("https://p16-common-sign.tiktokcdn-us.com/tos/seg.ts")]
    public void LooksLikeVideoSegment_AcceptsMediaUris(string url)
    {
        Assert.True(HlsMediaSegment.LooksLikeVideoSegment(url));
    }

    [Theory]
    [InlineData("https://p16-common-sign.tiktokcdn-us.com/tos/abc~tplv-tiktokx-origin.image?dr=1")]
    [InlineData("https://cdn.example.com/frame.image")]
    [InlineData("https://cdn.example.com/thumb.jpg")]
    public void LooksLikeVideoSegment_RejectsImages(string url)
    {
        Assert.False(HlsMediaSegment.LooksLikeVideoSegment(url));
    }

    [Fact]
    public void ContentTypeLooksLikeMedia_RejectsImageJpeg()
    {
        Assert.False(HlsMediaSegment.ContentTypeLooksLikeMedia("image/jpeg"));
        Assert.True(HlsMediaSegment.ContentTypeLooksLikeMedia("video/MP2T"));
        Assert.True(HlsMediaSegment.ContentTypeLooksLikeMedia(null));
    }
}
