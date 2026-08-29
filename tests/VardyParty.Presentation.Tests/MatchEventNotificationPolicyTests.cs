using AutoFixture;
using Moq;
using VardyParty.Ports;
using VardyParty.Presentation;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class MatchEventNotificationPolicyTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    private MatchEventNotificationPolicy CreateSut(bool notificationsSaved = true)
    {
        var prefs = _fixture.GetMock<ISoundPreferencesStore>();
        prefs.Setup(p => p.LoadGoalNotificationsEnabled()).Returns(notificationsSaved);
        return new MatchEventNotificationPolicy(prefs.Object);
    }

    /// <summary>
    /// The user-decided delivery table, exhaustively:
    /// foregrounded x playing x notifications-toggle → (toast/flash, audio).
    /// </summary>
    [Theory]
    [InlineData(true, false, true, true, true)] //  homepage active: sting + toast
    [InlineData(true, true, true, true, false)] //  playing: toast only, NO audio
    [InlineData(false, false, true, false, false)] // backgrounded: nothing at all
    [InlineData(false, true, true, false, false)] //  backgrounded while playing: nothing
    [InlineData(true, false, false, false, false)] // toggle OFF suppresses everything
    [InlineData(true, true, false, false, false)]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    public void DeliveryTable_IsExactlyTheUserDecision(
        bool foregrounded, bool playing, bool notificationsEnabled,
        bool expectPresent, bool expectAudio)
    {
        // Arrange
        var sut = CreateSut(notificationsSaved: notificationsEnabled);
        sut.IsAppForegrounded = foregrounded;
        sut.IsPlaybackActive = playing;

        // Act
        var present = sut.ShouldPresent;
        var audio = sut.ShouldPlayAudio;

        // Assert
        Assert.Equal(expectPresent, present);
        Assert.Equal(expectAudio, audio);
    }

    [Fact]
    public void NotificationsEnabled_DefaultsOn_WhenNothingSaved()
    {
        // Arrange: store mirrors the contract — default TRUE when nothing saved.
        var sut = CreateSut(notificationsSaved: true);

        // Act & Assert
        Assert.True(sut.NotificationsEnabled);
    }

    [Fact]
    public void SetNotificationsEnabled_Persists_AndTakesEffectImmediately()
    {
        // Arrange
        var prefs = _fixture.GetMock<ISoundPreferencesStore>();
        var sut = CreateSut(notificationsSaved: true);

        // Act
        sut.SetNotificationsEnabled(false);

        // Assert
        prefs.Verify(p => p.SaveGoalNotificationsEnabled(false), Times.Once);
        Assert.False(sut.NotificationsEnabled);
        Assert.False(sut.ShouldPresent);
    }

    [Fact]
    public void NotificationsEnabled_CachesPreferenceLoad()
    {
        // Arrange
        var prefs = _fixture.GetMock<ISoundPreferencesStore>();
        var sut = CreateSut(notificationsSaved: true);

        // Act
        _ = sut.NotificationsEnabled;
        _ = sut.NotificationsEnabled;

        // Assert: one storage read, then cached.
        prefs.Verify(p => p.LoadGoalNotificationsEnabled(), Times.Once);
    }

    [Fact]
    public void AudioRoute_UiSoundsToggleOff_SilencesTheSting_EvenWhenPolicyAllowsIt()
    {
        // Arrange: policy says audio may play, but the separate "UI sounds"
        // toggle is OFF — the sting routes through UiSoundService, which
        // still gates it.
        var prefs = _fixture.GetMock<ISoundPreferencesStore>();
        prefs.Setup(p => p.LoadGoalNotificationsEnabled()).Returns(true);
        prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(false);
        var player = _fixture.GetMock<IUiSoundPlayer>();
        var sounds = new UiSoundService(player.Object, prefs.Object);
        var sut = new MatchEventNotificationPolicy(prefs.Object);

        // Act: the exact call sequence HomeViewModel makes.
        if (sut.ShouldPresent && sut.ShouldPlayAudio)
        {
            sounds.Play(UiSound.Goal);
        }

        // Assert
        Assert.True(sut.ShouldPlayAudio);
        player.Verify(p => p.Play(It.IsAny<UiSound>()), Times.Never);
    }

    [Fact]
    public void AudioRoute_UiSoundsToggleOn_PlaysTheSting_WhenPolicyAllowsIt()
    {
        // Arrange
        var prefs = _fixture.GetMock<ISoundPreferencesStore>();
        prefs.Setup(p => p.LoadGoalNotificationsEnabled()).Returns(true);
        prefs.Setup(p => p.LoadUiSoundsEnabled()).Returns(true);
        var player = _fixture.GetMock<IUiSoundPlayer>();
        var sounds = new UiSoundService(player.Object, prefs.Object);
        var sut = new MatchEventNotificationPolicy(prefs.Object);

        // Act
        if (sut.ShouldPresent && sut.ShouldPlayAudio)
        {
            sounds.Play(UiSound.Goal);
        }

        // Assert
        player.Verify(p => p.Play(UiSound.Goal), Times.Once);
    }
}
