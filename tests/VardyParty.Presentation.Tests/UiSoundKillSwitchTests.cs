using VardyParty.Ports;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class UiSoundKillSwitchTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("true", false)]
    [InlineData("11", false)]
    public void IsDisabledValue_OnlyExactlyOne_DisablesSounds(string? environmentValue, bool expected)
    {
        // Arrange
        // (the environment value under test comes from the theory data)

        // Act
        var disabled = UiSoundKillSwitch.IsDisabledValue(environmentValue);

        // Assert
        Assert.Equal(expected, disabled);
    }

    [Fact]
    public void NullUiSoundPlayer_TheKillSwitchRegistration_IsANoOpPlayer()
    {
        // Arrange
        var player = new NullUiSoundPlayer();

        // Act
        var init = player.InitializeAsync();
        player.Play(UiSound.FocusMove);

        // Assert
        Assert.True(init.IsCompletedSuccessfully);
    }
}
