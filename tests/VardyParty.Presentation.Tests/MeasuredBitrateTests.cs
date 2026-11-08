using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class MeasuredBitrateTests
{
    [Theory]
    [InlineData(4500, 3_000_000UL, 1840, 1840)] // meter wins over AMS and encoding
    [InlineData(null, 3_000_000UL, 1840, 1840)]
    [InlineData(4500, 3_000_000UL, null, 3000)] // AMS when the meter has no window yet
    [InlineData(null, 0UL, 1840, 1840)] // AMS 0 (custom DownloadRequested) falls back to the meter
    [InlineData(4500, 0UL, null, 4500)] // encoding only when nothing was measured
    [InlineData(4500, 999UL, null, 4500)] // AMS below 1 kbps counts as unset
    [InlineData(0, 0UL, 0, null)]
    [InlineData(null, 0UL, null, null)]
    public void ResolveKbps_PrefersMeterThenAmsThenEncoding(
        int? encodingKbps,
        ulong amsBitsPerSecond,
        int? meterKbps,
        int? expected)
    {
        // Arrange
        // Act
        var kbps = MeasuredBitrate.ResolveKbps(encodingKbps, amsBitsPerSecond, meterKbps);

        // Assert
        Assert.Equal(expected, kbps);
    }
}
