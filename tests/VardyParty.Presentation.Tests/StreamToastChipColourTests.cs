using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

/// <summary>
/// Jon, 15 Nov 2026: the V1/V2 chip on the channel-start stream toast is coloured
/// like the video-info overlay badge, on every platform, from one shared mapping.
/// </summary>
public class StreamToastChipColourTests
{
    [Fact]
    public void Toast_V2Chip_UsesTheVideoInfoBadgeColours()
    {
        // Arrange
        var toast = new StreamToastModel(2, 9, "1280x720", "V2", "North Sports");

        // Act
        var parts = toast.Parts;

        // Assert
        Assert.Equal("2/9 · V2 · North Sports · 720p", toast.Text);
        Assert.True(parts.HasChip);
        Assert.Equal("2/9 · ", parts.Prefix);
        Assert.Equal("V2", parts.Chip);
        Assert.Equal(" · North Sports · 720p", parts.Suffix);
        Assert.Equal(SourceBadgeStyle.ForLabel("V2"), parts.ChipStyle);
        Assert.Equal(toast.Text, parts.Text);
    }

    [Fact]
    public void Toast_V1Chip_UsesTheBlueBadge_V2UsesThePurpleBadge()
    {
        // Arrange
        var v1 = new StreamToastModel(1, 3, null, "V1", "Harbour TV");
        var v2 = new StreamToastModel(2, 3, null, "V2", "Harbour TV");

        // Act
        var v1Style = v1.Parts.ChipStyle;
        var v2Style = v2.Parts.ChipStyle;

        // Assert
        Assert.Equal("#1e3a5f", v1Style?.BackgroundHex);
        Assert.Equal("#93c5fd", v1Style?.ForegroundHex);
        Assert.Equal("#3b0764", v2Style?.BackgroundHex);
        Assert.Equal("#d8b4fe", v2Style?.ForegroundHex);
        Assert.Equal(SourceBadgeStyle.ForLabel("V1"), v1Style);
    }

    [Fact]
    public void Toast_ChipAtTheEnd_HasAnEmptySuffix()
    {
        // Arrange
        const string row = "4/5 · V1";

        // Act
        var parts = PlayerOverlayFormatter.SplitSourceChip(row, "V1");

        // Assert
        Assert.Equal("4/5 · ", parts.Prefix);
        Assert.Equal("V1", parts.Chip);
        Assert.Equal(string.Empty, parts.Suffix);
    }

    [Fact]
    public void Toast_NoSourceLabel_OrLabelNotOnTheRow_HasNoChip()
    {
        // Arrange
        const string row = "1/2 · V2X Sports · 720p";

        // Act
        var noLabel = PlayerOverlayFormatter.SplitSourceChip(row, null);
        var notWholeToken = PlayerOverlayFormatter.SplitSourceChip(row, "V2");
        var empty = PlayerOverlayFormatter.SplitSourceChip(null, "V2");

        // Assert
        Assert.False(noLabel.HasChip);
        Assert.Equal(row, noLabel.Text);
        Assert.False(notWholeToken.HasChip);
        Assert.Equal(row, notWholeToken.Text);
        Assert.False(empty.HasChip);
        Assert.Equal(string.Empty, empty.Text);
    }
}
