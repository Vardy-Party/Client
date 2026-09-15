using Xunit;
using VardyParty.LocalService.V2;

namespace VardyParty.Streaming.Tests;

public sealed class MpDiscoveredChipsTests
{
    [Fact]
    public void Normalize_KeepsLeafChipsAndDropsStoreButtons()
    {
        // Arrange
        var normalizer = new V2PlaybackTransportPlugin();

        // Act
        var chips = normalizer.Normalize(
        [
            "APK TV TG Chip A Chip B",
            "Chip A",
            "Chip B",
            "APK",
            "TV"
        ]);

        // Assert
        Assert.Equal(["Chip A", "Chip B"], chips);
    }
}
