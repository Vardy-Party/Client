using System;
using Microsoft.Extensions.Time.Testing;
using Moq;
using VardyParty.Ports;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class UiSoundServiceTests
{
    private readonly Mock<IUiSoundPlayer> _player = new();
    private readonly Mock<ISoundPreferencesStore> _prefs = new();
    private readonly FakeTimeProvider _time = new();

    private UiSoundService CreateSut()
    {
        return new UiSoundService(_player.Object, _prefs.Object, _time);
    }

    [Fact]
    public void Play_DefaultsOn_WhenNothingSaved()
    {
        // Arrange: store mirrors the contract — default TRUE when nothing saved.
        _prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(true);
        var sut = CreateSut();

        // Act
        sut.Play(UiSound.Select);

        // Assert
        _player.Verify(p => p.Play(UiSound.Select), Times.Once);
    }

    [Fact]
    public void Play_DoesNothing_WhenDisabledInPreferences()
    {
        // Arrange
        _prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(false);
        var sut = CreateSut();

        // Act
        sut.Play(UiSound.Select);
        sut.Play(UiSound.Goal);

        // Assert
        _player.Verify(p => p.Play(It.IsAny<UiSound>()), Times.Never);
    }

    [Fact]
    public void Play_DoesNothing_WhileSuppressed()
    {
        // Arrange: playback visible — nothing may play over commentary.
        _prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(true);
        var sut = CreateSut();
        sut.SuppressAll = true;

        // Act
        sut.Play(UiSound.FocusMove);
        sut.Play(UiSound.Goal);
        sut.Play(UiSound.Error);

        // Assert
        _player.Verify(p => p.Play(It.IsAny<UiSound>()), Times.Never);
    }

    [Fact]
    public void Play_ResumesAfterSuppressionLifts()
    {
        // Arrange
        _prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(true);
        var sut = CreateSut();
        sut.SuppressAll = true;
        sut.Play(UiSound.Select);

        // Act
        sut.SuppressAll = false;
        sut.Play(UiSound.Select);

        // Assert
        _player.Verify(p => p.Play(UiSound.Select), Times.Once);
    }

    [Fact]
    public void Play_ThrottlesFocusTicks_Within40Ms()
    {
        // Arrange: D-pad autorepeat spams focus moves.
        _prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(true);
        var sut = CreateSut();

        // Act: three ticks 10ms apart — only the first may play.
        sut.Play(UiSound.FocusMove);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        sut.Play(UiSound.FocusMove);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        sut.Play(UiSound.FocusMove);

        // Assert
        _player.Verify(p => p.Play(UiSound.FocusMove), Times.Once);
    }

    [Fact]
    public void Play_AllowsFocusTick_AfterThrottleWindow()
    {
        // Arrange
        _prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(true);
        var sut = CreateSut();

        // Act
        sut.Play(UiSound.FocusMove);
        _time.Advance(TimeSpan.FromMilliseconds(41));
        sut.Play(UiSound.FocusMove);

        // Assert
        _player.Verify(p => p.Play(UiSound.FocusMove), Times.Exactly(2));
    }

    [Fact]
    public void Play_DoesNotThrottle_NonFocusSounds()
    {
        // Arrange
        _prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(true);
        var sut = CreateSut();

        // Act: two selects back to back both play.
        sut.Play(UiSound.Select);
        sut.Play(UiSound.Select);

        // Assert
        _player.Verify(p => p.Play(UiSound.Select), Times.Exactly(2));
    }

    [Fact]
    public void SetEnabled_True_PersistsAndPlaysSelectConfirmation()
    {
        // Arrange
        _prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(false);
        var sut = CreateSut();

        // Act
        sut.SetEnabled(true);

        // Assert
        _prefs.Verify(p => p.SaveUiSoundsEnabled(true), Times.Once);
        _player.Verify(p => p.Play(UiSound.Select), Times.Once);
        Assert.True(sut.Enabled);
    }

    [Fact]
    public void SetEnabled_False_PersistsAndPlaysNothing()
    {
        // Arrange
        _prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(true);
        var sut = CreateSut();

        // Act
        sut.SetEnabled(false);

        // Assert
        _prefs.Verify(p => p.SaveUiSoundsEnabled(false), Times.Once);
        _player.Verify(p => p.Play(It.IsAny<UiSound>()), Times.Never);
        Assert.False(sut.Enabled);
    }

    [Fact]
    public void Enabled_CachesPreferenceLoad()
    {
        // Arrange
        _prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(true);
        var sut = CreateSut();

        // Act
        _ = sut.Enabled;
        _ = sut.Enabled;
        sut.Play(UiSound.Select);

        // Assert: one storage read, then cached.
        _prefs.Verify(p => p.LoadUiSoundsEnabled(), Times.Once);
    }
}
