using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Ports;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class PlaybackAudioSessionTests
{
    [Fact]
    public void Plan_WhenPlaybackVisible_SuppressesAndYields()
    {
        var plan = PlaybackAudioSession.Plan(playbackVisible: true);

        Assert.True(plan.SuppressAll);
        Assert.True(plan.YieldDevice);
        Assert.False(plan.RecoverDevice);
    }

    [Fact]
    public void Plan_WhenPlaybackHidden_UnsuppressesAndRecovers()
    {
        var plan = PlaybackAudioSession.Plan(playbackVisible: false);

        Assert.False(plan.SuppressAll);
        Assert.False(plan.YieldDevice);
        Assert.True(plan.RecoverDevice);
    }

    [Fact]
    public void Apply_WhenVisible_SuppressesUiSoundsAndYieldsDevice()
    {
        var player = new RecordingUiSoundPlayer();
        var sounds = new UiSoundService(player, new InMemorySoundPreferencesStore());

        PlaybackAudioSession.Apply(playbackVisible: true, sounds, player);
        sounds.Play(UiSound.Select);

        Assert.True(sounds.SuppressAll);
        Assert.Equal(new[] { "Yield" }, player.Calls);
    }

    [Fact]
    public void Apply_WhenHidden_RestoresUiSoundsAndRecoversDevice()
    {
        var player = new RecordingUiSoundPlayer();
        var sounds = new UiSoundService(player, new InMemorySoundPreferencesStore());
        PlaybackAudioSession.Apply(playbackVisible: true, sounds, player);

        PlaybackAudioSession.Apply(playbackVisible: false, sounds, player);
        sounds.Play(UiSound.Select);

        Assert.False(sounds.SuppressAll);
        Assert.Equal(new[] { "Yield", "Recover", "Play:Select" }, player.Calls);
    }

    [Fact]
    public void Apply_AfterFailedSession_SameRestorePathAsClose()
    {
        var player = new RecordingUiSoundPlayer();
        var sounds = new UiSoundService(player, new InMemorySoundPreferencesStore());

        PlaybackAudioSession.Apply(playbackVisible: true, sounds, player);
        PlaybackAudioSession.Apply(playbackVisible: false, sounds, player);

        Assert.False(sounds.SuppressAll);
        Assert.Equal(new[] { "Yield", "Recover" }, player.Calls);
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
