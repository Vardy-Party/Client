using VardyParty.Ports;

namespace VardyParty.Presentation;

/// <summary>
/// Playback-visibility audio policy shared by every head.
///
/// Two things must happen around a stream session, not just
/// <see cref="UiSoundService.SuppressAll"/>:
/// <list type="bullet">
///   <item>Yield the UI-sound device BEFORE the video engine starts so the
///   mixer is free (desktop: Pulse/ALSA; Android: SoundPool vs ExoPlayer).
///   Holding the device leaves video silent and poisons the UI engine.</item>
///   <item>Un-suppress AND recover the UI-sound engine AFTER Close / a
///   failed session / the host page reappears. Suppress-only left a dead
///   device in place — field: Android TV ticks stayed silent until the
///   user toggled Settings → UI sounds off and on.</item>
/// </list>
/// Device recovery itself is platform-specific (<see cref="IUiSoundPlayer.YieldDevice"/>
/// / <see cref="IUiSoundPlayer.RecoverDevice"/>); this type is the testable
/// decision table.
/// </summary>
public readonly record struct PlaybackAudioSessionPlan(bool SuppressAll, bool YieldDevice, bool RecoverDevice);

public static class PlaybackAudioSession
{
    public static PlaybackAudioSessionPlan Plan(bool playbackVisible) =>
        playbackVisible
            ? new PlaybackAudioSessionPlan(SuppressAll: true, YieldDevice: true, RecoverDevice: false)
            : new PlaybackAudioSessionPlan(SuppressAll: false, YieldDevice: false, RecoverDevice: true);

    /// <summary>
    /// Apply <see cref="Plan"/>: set suppress, then yield or recover the
    /// player. Yield is synchronous so the mixer is released before the
    /// caller hands the video engine the device. Recover is fire-and-forget
    /// inside the player.
    /// </summary>
    public static void Apply(bool playbackVisible, UiSoundService sounds, IUiSoundPlayer player)
    {
        ArgumentNullException.ThrowIfNull(sounds);
        ArgumentNullException.ThrowIfNull(player);

        var plan = Plan(playbackVisible);
        sounds.SuppressAll = plan.SuppressAll;
        if (plan.YieldDevice)
        {
            player.YieldDevice();
        }

        if (plan.RecoverDevice)
        {
            player.RecoverDevice();
        }
    }
}
