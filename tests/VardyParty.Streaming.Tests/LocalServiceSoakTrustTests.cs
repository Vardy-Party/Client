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
    public void ShouldTrust_False_WhenNoSoakEvidence()
    {
        // Arrange
        var response = new M3U8Response
        {
            Url = "https://streams.example.test/list.m3u8",
            RequestHeaders = new Dictionary<string, string> { ["referer"] = "https://player.example.test/" }
        };

        // Act
        var trust = LocalServiceSoakTrust.ShouldTrust(response, StreamHealthStatus.ManifestUnreachable);

        // Assert
        Assert.False(trust);
    }
}
