using System;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

public class PlaybackContentTypesTests
{
    [Theory]
    [InlineData("text/plain; charset=utf-8", "/live/token", "Manifest", PlaybackContentTypes.MpegUrl)]
    [InlineData("application/vnd.apple.mpegurl", "/no-extension", "Manifest", PlaybackContentTypes.MpegUrl)]
    [InlineData(null, "/segment", "MediaSegment", PlaybackContentTypes.MpegTs)]
    [InlineData("application/octet-stream", "/segment", "MediaSegment", PlaybackContentTypes.MpegTs)]
    [InlineData("application/octet-stream; charset=binary", "/segment", "InitializationSegment", PlaybackContentTypes.MpegTs)]
    [InlineData("video/mp4", "/seg.ts", "MediaSegment", PlaybackContentTypes.MpegTs)]
    [InlineData("text/plain", "/seg.ts", "MediaSegment", PlaybackContentTypes.MpegTs)]
    [InlineData("text/plain", "/init.mp4", "MediaSegment", "text/plain")]
    [InlineData("text/plain; charset=utf-8", "/fmp4.m4s", "MediaSegment", "text/plain")]
    [InlineData("", "/fmp4.m4s", "MediaSegment", PlaybackContentTypes.OctetStream)]
    [InlineData("video/mp4; charset=binary", "/fmp4.m4s", "InitializationSegment", "video/mp4")]
    [InlineData("audio/aac", "/audio.aac", "MediaSegment", "audio/aac")]
    public void Resolve_ClassifiesOnMediaType(string? contentType, string path, string kind, string expected)
    {
        // Arrange
        var resource = Enum.Parse<PlaybackResourceKind>(kind);

        // Act
        var resolved = PlaybackContentTypes.Resolve(contentType, path, resource);

        // Assert
        Assert.Equal(expected, resolved);
    }
}
