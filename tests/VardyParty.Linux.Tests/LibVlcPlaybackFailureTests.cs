using VardyParty.Linux.Services;
using Xunit;

namespace VardyParty.Linux.Tests;

public class LibVlcPlaybackFailureTests
{
    [Theory]
    [InlineData("http", "HTTP 403 Forbidden")]
    [InlineData("access", "HTTP/1.1 403 Forbidden")]
    [InlineData("access/http", "http/1.0 403")]
    [InlineData(null, "HTTP 403")]
    public void IsHttpForbidden_MatchesHttpStatusLine(string? module, string message)
    {
        // Arrange / Act
        var forbidden = LibVlcPlaybackFailure.IsHttpForbidden(module, message);

        // Assert
        Assert.True(forbidden);
    }

    [Theory]
    [InlineData("access", "bandwidth 4032000")]
    [InlineData("access", "error 4032")]
    [InlineData("access", "HTTP 4030")]
    [InlineData("adaptive", "failed with 403 inside 4032")]
    [InlineData("access", "segment duration 4.032")]
    [InlineData(null, "")]
    [InlineData("access", null)]
    public void IsHttpForbidden_DoesNotTreatIncidental403DigitsAsCdnReject(string? module, string? message)
    {
        // Arrange / Act
        var forbidden = LibVlcPlaybackFailure.IsHttpForbidden(module, message);

        // Assert — false failover on Linux/WSL kills a playable HLS URL
        Assert.False(forbidden);
    }

    [Fact]
    public void IsFatalAdaptiveDemux_WhenAdaptiveCannotCreateDemuxer()
    {
        // Arrange
        const string module = "adaptive";
        const string message = "Failed to create demuxer (nil)";

        // Act
        var fatal = LibVlcPlaybackFailure.IsFatalAdaptiveDemux(module, message);

        // Assert
        Assert.True(fatal);
    }

    [Fact]
    public void IsFatalAdaptiveDemux_IgnoresDecoderFailures()
    {
        // Arrange
        const string module = "adaptive";
        const string message = "Failed to create decoder";

        // Act
        var fatal = LibVlcPlaybackFailure.IsFatalAdaptiveDemux(module, message);

        // Assert
        Assert.False(fatal);
    }
}
