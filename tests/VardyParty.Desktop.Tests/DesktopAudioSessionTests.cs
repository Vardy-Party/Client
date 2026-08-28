using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Desktop.Services;
using VardyParty.Ports;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Desktop.Tests;

public class DesktopAudioSessionTests
{
    [Fact]
    public void Plan_WhenPlaybackVisible_SuppressesAndYields()
    {
        // Arrange
        // Act
        var plan = DesktopAudioSession.Plan(playbackVisible: true);

        // Assert
        Assert.True(plan.SuppressAll);
        Assert.True(plan.YieldDevice);
        Assert.False(plan.RecoverDevice);
    }

    [Fact]
    public void Plan_WhenPlaybackHidden_UnsuppressesAndRecovers()
    {
        // Arrange
        // Act
        var plan = DesktopAudioSession.Plan(playbackVisible: false);

        // Assert
        Assert.False(plan.SuppressAll);
        Assert.False(plan.YieldDevice);
        Assert.True(plan.RecoverDevice);
    }

    [Fact]
    public void Apply_WhenVisible_SuppressesUiSoundsAndYieldsDevice()
    {
        // Arrange
        var player = new RecordingUiSoundPlayer();
        var sounds = new UiSoundService(player, new AlwaysOnPreferences());

        // Act
        DesktopAudioSession.Apply(playbackVisible: true, sounds, player);
        sounds.Play(UiSound.Select);

        // Assert
        Assert.True(sounds.SuppressAll);
        Assert.Equal(new[] { "Yield" }, player.Calls);
    }

    [Fact]
    public void Apply_WhenHidden_RestoresUiSoundsAndRecoversDevice()
    {
        // Arrange
        var player = new RecordingUiSoundPlayer();
        var sounds = new UiSoundService(player, new AlwaysOnPreferences());
        DesktopAudioSession.Apply(playbackVisible: true, sounds, player);

        // Act
        DesktopAudioSession.Apply(playbackVisible: false, sounds, player);
        sounds.Play(UiSound.Select);

        // Assert
        Assert.False(sounds.SuppressAll);
        Assert.Equal(new[] { "Yield", "Recover", "Play:Select" }, player.Calls);
    }

    [Fact]
    public void Apply_AfterFailedSession_SameRestorePathAsClose()
    {
        // Arrange: visibility went true (stream attempt) then false (no streams / error).
        var player = new RecordingUiSoundPlayer();
        var sounds = new UiSoundService(player, new AlwaysOnPreferences());

        // Act
        DesktopAudioSession.Apply(playbackVisible: true, sounds, player);
        DesktopAudioSession.Apply(playbackVisible: false, sounds, player);

        // Assert
        Assert.False(sounds.SuppressAll);
        Assert.Equal(new[] { "Yield", "Recover" }, player.Calls);
    }

    private sealed class AlwaysOnPreferences : ISoundPreferencesStore
    {
        public bool LoadUiSoundsEnabled() => true;

        public void SaveUiSoundsEnabled(bool enabled)
        {
        }
    }

    private sealed class RecordingUiSoundPlayer : IUiSoundPlayer
    {
        public List<string> Calls { get; } = new();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Play(UiSound sound) => Calls.Add($"Play:{sound}");

        public void YieldDevice() => Calls.Add("Yield");

        public void RecoverDevice() => Calls.Add("Recover");
    }
}
