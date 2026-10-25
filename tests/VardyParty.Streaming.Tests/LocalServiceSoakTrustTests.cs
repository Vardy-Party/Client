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
        var response = new M3U8Response
        {
            Url = "https://streams.example.test/list.m3u8",
            RequestHeaders = new Dictionary<string, string> { ["referer"] = "https://player.example.test/" }
        };

        Assert.True(LocalServiceSoakTrust.ShouldSkipClientCdnProbe(response));
    }

    [Fact]
    public void ShouldSkipClientCdnProbe_False_WithoutSoak()
    {
        var response = new M3U8Response
        {
            Url = "https://streams.example.test/list.m3u8",
            RequestHeaders = new Dictionary<string, string> { ["user-agent"] = "TestAgent/1.0" }
        };

        Assert.False(LocalServiceSoakTrust.ShouldSkipClientCdnProbe(response));
    }

    [Fact]
    public void RequiresFreshM3u8ForPlayback_WhenPoolRowHasRefererSoak()
    {
        var stream = new EnrichedStream
        {
            Stream = new Stream { Url = "https://page.example.test/x", Channel = "FB" },
            ResolvedM3U8Url = "https://cdn.example.test/a.m3u8?t=1",
            RequestHeaders = new Dictionary<string, string> { ["referer"] = "https://embed.example.test/" }
        };

        Assert.True(LocalServiceSoakTrust.RequiresFreshM3u8ForPlayback(stream));
    }
}
