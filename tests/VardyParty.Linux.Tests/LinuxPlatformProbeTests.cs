using System;
using System.Linq;
using VardyParty.Linux.Services;
using Xunit;

namespace VardyParty.Linux.Tests;

public class LinuxPlatformProbeTests
{
    [Fact]
    public void BuildLibVlcOptions_Conservative_PinsSoftwareDecodeX11AndPulse()
    {
        // Arrange
        // Act
        var options = LinuxPlatformProbe.BuildLibVlcOptions(conservative: true);

        // Assert
        Assert.Contains("--avcodec-hw=none", options);
        Assert.Contains("--vout=x11", options);
        Assert.DoesNotContain("--demux=avformat", options);
        Assert.Contains("--aout=pulse", options);
        Assert.DoesNotContain(options, o => o.Contains("no-audio", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildLibVlcOptions_Native_PrefersHardwareAndPulseAout()
    {
        // Arrange
        // Act
        var options = LinuxPlatformProbe.BuildLibVlcOptions(conservative: false);

        // Assert
        Assert.Contains("--avcodec-hw=any", options);
        Assert.Contains("--aout=pulse", options);
        Assert.DoesNotContain("--vout=x11", options);
        Assert.DoesNotContain("--demux=avformat", options);
        Assert.DoesNotContain(options, o => o.Contains("no-audio", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildLibVlcOptions_AlwaysKeepsSharedBaseFlags()
    {
        // Arrange
        // Act
        var conservative = LinuxPlatformProbe.BuildLibVlcOptions(conservative: true);
        var native = LinuxPlatformProbe.BuildLibVlcOptions(conservative: false);

        // Assert
        foreach (var options in new[] { conservative, native })
        {
            Assert.Contains("--quiet", options);
            Assert.Contains("--no-video-title-show", options);
            Assert.Contains("--network-caching=3000", options);
            Assert.Contains("--live-caching=3000", options);
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
        var options = LinuxPlatformProbe.BuildLibVlcOptions(conservative: true, overrideModule);

        // Assert
        Assert.True(LinuxPlatformProbe.TryNormalizeAudioOutput(overrideModule, out var normalized));
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
        var conservative = LinuxPlatformProbe.ResolveAudioOutputModule(conservative: true, overrideModule);
        var native = LinuxPlatformProbe.ResolveAudioOutputModule(conservative: false, overrideModule);

        // Assert
        Assert.Equal(LinuxPlatformProbe.PulseAudioOutput, conservative);
        Assert.Equal(LinuxPlatformProbe.PulseAudioOutput, native);
    }

    [Fact]
    public void ResolveAudioOutputModule_AnyOverride_StillHonoured()
    {
        // Arrange
        // Act
        var module = LinuxPlatformProbe.ResolveAudioOutputModule(conservative: false, "any");

        // Assert
        Assert.Equal(LinuxPlatformProbe.AnyAudioOutput, module);
    }

    [Fact]
    public void DescribeAudioEnvironment_IncludesAoutAndPulseContext()
    {
        // Arrange
        // Act
        var description = LinuxPlatformProbe.DescribeAudioEnvironment(
            audioOutputModule: "pulse",
            pulseServer: "unix:/tmp/pulse",
            runtimeDir: "/run/user/1000",
            isWsl: true);

        // Assert
        Assert.Contains("aout=pulse", description, StringComparison.Ordinal);
        Assert.Contains("wsl=True", description, StringComparison.Ordinal);
        Assert.Contains("PULSE_SERVER=unix:/tmp/pulse", description, StringComparison.Ordinal);
        Assert.Contains("XDG_RUNTIME_DIR=set", description, StringComparison.Ordinal);
    }

    [Fact]
    public void IsPinnedAudioOutput_PulseAndAlsaOnly()
    {
        // Arrange
        // Act
        var pulse = LinuxPlatformProbe.IsPinnedAudioOutput("pulse");
        var alsa = LinuxPlatformProbe.IsPinnedAudioOutput("ALSA");
        var any = LinuxPlatformProbe.IsPinnedAudioOutput("any");
        var dummy = LinuxPlatformProbe.IsPinnedAudioOutput("dummy");

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

        var options = LinuxPlatformProbe.BuildPlaybackMediaOptions(
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
