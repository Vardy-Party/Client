using System.Collections.Generic;
using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public class LocalServiceSoakTrustTests
{
    [Fact]
    public void ShouldTrust_RewrittenSegments_WhenCdnUnreachable()
    {
        // Arrange
        var response = new M3U8Response
        {
            Url = "https://streams.example.test/list.m3u8",
            RewrittenSegments = ["https://media.example.test/cfall/seg.exe?_s2=1"]
        };

        // Act
        var trust = LocalServiceSoakTrust.ShouldTrust(response, StreamHealthStatus.SegmentUnreachable);

        // Assert
        Assert.True(trust);
    }

    [Fact]
    public void ShouldTrust_CookieHeaders_WhenManifestUnreachable()
    {
        // Arrange
        var response = new M3U8Response
        {
            Url = "https://streams.example.test/list.m3u8",
            RequestHeaders = new Dictionary<string, string> { ["cookie"] = "a=1" }
        };

        // Act
        var trust = LocalServiceSoakTrust.ShouldTrust(response, StreamHealthStatus.ManifestUnreachable);

        // Assert
        Assert.True(trust);
    }

    [Fact]
    public void ShouldTrust_RefererHeaders_WhenManifestUnreachable()
    {
        // Arrange — V1 /play stamps Referer even when Cookie is missing
        var response = new M3U8Response
        {
            Url = "https://streams.example.test/list.m3u8",
            RequestHeaders = new Dictionary<string, string> { ["referer"] = "https://player.example.test/" }
        };

        // Act
        var trust = LocalServiceSoakTrust.ShouldTrust(response, StreamHealthStatus.ManifestUnreachable);

        // Assert
        Assert.True(trust);
    }

    [Fact]
    public void ShouldTrust_False_WhenNoSoakEvidence()
    {
        // Arrange
        var response = new M3U8Response
        {
            Url = "https://streams.example.test/list.m3u8",
            RequestHeaders = new Dictionary<string, string> { ["user-agent"] = "TestAgent/1.0" }
        };

        // Act
        var trust = LocalServiceSoakTrust.ShouldTrust(response, StreamHealthStatus.ManifestUnreachable);

        // Assert
        Assert.False(trust);
    }

    [Fact]
    public void ShouldSkipClientCdnProbe_WhenRefererSoakPresent()
    {
        // Arrange
        var response = new M3U8Response
        {
            Url = "https://streams.example.test/list.m3u8",
            RequestHeaders = new Dictionary<string, string> { ["referer"] = "https://player.example.test/" }
        };

        // Act
        var skip = LocalServiceSoakTrust.ShouldSkipClientCdnProbe(response);

        // Assert
        Assert.True(skip);
    }

    [Fact]
    public void ShouldSkipClientCdnProbe_False_WithoutSoak()
    {
        // Arrange
        var response = new M3U8Response
        {
            Url = "https://streams.example.test/list.m3u8",
            RequestHeaders = new Dictionary<string, string> { ["user-agent"] = "TestAgent/1.0" }
        };

        // Act
        var skip = LocalServiceSoakTrust.ShouldSkipClientCdnProbe(response);

        // Assert
        Assert.False(skip);
    }

    [Fact]
    public void RequiresFreshM3u8ForPlayback_WhenPoolRowHasRefererSoak()
    {
        // Arrange
        var stream = new EnrichedStream
        {
            Stream = new Stream { Url = "https://page.example.test/x", Channel = "FB" },
            ResolvedM3U8Url = "https://cdn.example.test/a.m3u8?t=1",
            RequestHeaders = new Dictionary<string, string> { ["referer"] = "https://embed.example.test/" }
        };

        // Act
        var requiresFresh = LocalServiceSoakTrust.RequiresFreshM3u8ForPlayback(stream);

        // Assert
        Assert.True(requiresFresh);
    }
}
