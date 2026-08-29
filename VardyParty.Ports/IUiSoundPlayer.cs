namespace VardyParty.Ports;

/// <summary>
/// Platform sound output for the six UI sounds. Implementations preload and
/// decode everything in <see cref="InitializeAsync"/> (called once on a
/// background task after first render — never in the startup path);
/// <see cref="Play"/> must be fire-and-forget, non-blocking, and must never
/// lazy-load or throw (audio loss degrades silently).
/// </summary>
public interface IUiSoundPlayer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    void Play(UiSound sound);

    /// <summary>
    /// Release the OS audio device so another engine (desktop libvlc) can
    /// open Pulse/ALSA. Default is a no-op — Android/Windows players do not
    /// share a device with the video engine.
    /// </summary>
    void YieldDevice()
    {
    }

    /// <summary>
    /// Re-open the OS audio device after exclusive video playback ended
    /// (or after a failed/no-streams session that yielded). Default no-op.
    /// Implementations that can fault permanently MUST rebuild here, not
    /// just flip a mute flag.
    /// </summary>
    void RecoverDevice()
    {
    }
}
