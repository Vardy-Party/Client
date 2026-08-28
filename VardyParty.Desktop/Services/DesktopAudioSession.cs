using System;
using VardyParty.Ports;
using VardyParty.Presentation;

namespace VardyParty.Desktop.Services;

/// <summary>
/// Playback-visibility audio policy for the desktop head.
///
/// Two things must happen around a libvlc session, not just
/// <see cref="UiSoundService.SuppressAll"/>:
/// <list type="bullet">
///   <item>Yield the SoundFlow/miniaudio device BEFORE Play so Pulse/ALSA is
///   free for libvlc (otherwise video is silent and the UI-sound engine
///   faults).</item>
///   <item>Un-suppress AND recover the UI-sound engine AFTER Close / a
///   failed session so homepage ticks work again. Suppress-only left the
///   poisoned miniaudio device in place — field: "sounds stay dead after
///   Close".</item>
/// </list>
/// Device recovery itself is platform-specific (<see cref="IUiSoundPlayer.YieldDevice"/>
/// / <see cref="IUiSoundPlayer.RecoverDevice"/>); this type is the testable
/// decision table.
/// </summary>
public readonly record struct DesktopAudioSessionPlan(bool SuppressAll, bool YieldDevice, bool RecoverDevice);

public static class DesktopAudioSession
{
    public static DesktopAudioSessionPlan Plan(bool playbackVisible) =>
        playbackVisible
            ? new DesktopAudioSessionPlan(SuppressAll: true, YieldDevice: true, RecoverDevice: false)
            : new DesktopAudioSessionPlan(SuppressAll: false, YieldDevice: false, RecoverDevice: true);

    /// <summary>
    /// Apply <see cref="Plan"/>: set suppress, then yield or recover the
    /// player. Yield is synchronous so Pulse is released before the caller
    /// hands libvlc the device. Recover is fire-and-forget inside the player.
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
