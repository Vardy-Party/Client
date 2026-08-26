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
}
