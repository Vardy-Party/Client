using System;
using System.Linq;
using VardyParty.Desktop.Services;
using Xunit;

namespace VardyParty.Desktop.Tests;

public class DesktopPlatformProbeTests
{
    [Fact]
    public void BuildLibVlcOptions_Conservative_PinsSoftwareDecodeX11AndPulse()
    {
        // Arrange
        // Act
        var options = DesktopPlatformProbe.BuildLibVlcOptions(conservative: true);

        // Assert
        Assert.Contains("--avcodec-hw=none", options);
        Assert.Contains("--vout=x11", options);
        Assert.DoesNotContain("--demux=avformat", options);
        Assert.Contains("--aout=pulse", options);
        Assert.DoesNotContain(options, o => o.Contains("no-audio", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildLibVlcOptions_Native_PrefersHardwareAndAnyAout()
    {
        // Arrange
        // Act
        var options = DesktopPlatformProbe.BuildLibVlcOptions(conservative: false);

        // Assert
        Assert.Contains("--avcodec-hw=any", options);
        Assert.Contains("--aout=any", options);
        Assert.DoesNotContain("--vout=x11", options);
        Assert.DoesNotContain("--demux=avformat", options);
        Assert.DoesNotContain(options, o => o.Contains("no-audio", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildLibVlcOptions_AlwaysKeepsSharedBaseFlags()
    {
        // Arrange
        // Act
        var conservative = DesktopPlatformProbe.BuildLibVlcOptions(conservative: true);
        var native = DesktopPlatformProbe.BuildLibVlcOptions(conservative: false);

        // Assert
        foreach (var options in new[] { conservative, native })
        {
            Assert.Contains("--quiet", options);
            Assert.Contains("--no-video-title-show", options);
            Assert.Contains("--network-caching=2000", options);
            Assert.Contains("--http-reconnect", options);
            Assert.Contains("--no-spdif", options);
            Assert.Equal(1, options.Count(o => o.StartsWith("--aout=", StringComparison.Ordinal)));
        }
    }

    [Theory]
    [InlineData("pulse")]
    [InlineData("alsa")]
    [InlineData("any")]
    [InlineData("PULSE")]
    [InlineData(" Alsa ")]
    public void BuildLibVlcOptions_Override_PinsRequestedAout(string overrideModule)
    {
        // Arrange
        // Act
        var options = DesktopPlatformProbe.BuildLibVlcOptions(conservative: true, overrideModule);

        // Assert
        Assert.True(DesktopPlatformProbe.TryNormalizeAudioOutput(overrideModule, out var normalized));
        Assert.Contains($"--aout={normalized}", options);
        Assert.DoesNotContain(options, o => o.Contains("no-audio", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dummy")]
    [InlineData("no-audio")]
    [InlineData("--no-audio")]
    [InlineData("jack")]
    public void ResolveAudioOutputModule_RejectsUnsafeOrUnknownOverrides(string? overrideModule)
    {
        // Arrange
        // Act
        var conservative = DesktopPlatformProbe.ResolveAudioOutputModule(conservative: true, overrideModule);
        var native = DesktopPlatformProbe.ResolveAudioOutputModule(conservative: false, overrideModule);

        // Assert
        Assert.Equal(DesktopPlatformProbe.PulseAudioOutput, conservative);
        Assert.Equal(DesktopPlatformProbe.AnyAudioOutput, native);
    }

    [Fact]
    public void IsPinnedAudioOutput_PulseAndAlsaOnly()
    {
        // Arrange
        // Act
        var pulse = DesktopPlatformProbe.IsPinnedAudioOutput("pulse");
        var alsa = DesktopPlatformProbe.IsPinnedAudioOutput("ALSA");
        var any = DesktopPlatformProbe.IsPinnedAudioOutput("any");
        var dummy = DesktopPlatformProbe.IsPinnedAudioOutput("dummy");

        // Assert
        Assert.True(pulse);
        Assert.True(alsa);
        Assert.False(any);
        Assert.False(dummy);
    }

    [Fact]
    public void BuildPlaybackMediaOptions_PassesUnquotedRefererAndAvformatHeaders()
    {
        const string referer = "https://hamis.romponalis.st/";
        const string userAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36";

        var options = DesktopPlatformProbe.BuildPlaybackMediaOptions(
            conservative: true, referer, userAgent);

        Assert.Contains($":http-referrer={referer}", options);
        Assert.Contains($":http-user-agent={userAgent}", options);
        Assert.DoesNotContain(options, o => o.Contains(":http-referrer=\"", StringComparison.Ordinal));
        Assert.Contains(options, o =>
            o.StartsWith(":avformat-options=headers=", StringComparison.Ordinal) &&
            o.Contains($"Referer: {referer}", StringComparison.Ordinal) &&
            o.Contains("Origin: https://hamis.romponalis.st", StringComparison.Ordinal));
        Assert.DoesNotContain(options, o => o.Equals(":demux=avformat", StringComparison.Ordinal));
    }
}
