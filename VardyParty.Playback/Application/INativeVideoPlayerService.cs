using VardyParty.Ports;

namespace VardyParty.Playback;

public interface INativeVideoPlayerService : IPlaybackLauncher
{
    /// <summary>
    /// Raised with true when the native player surface becomes visible and
    /// false when it is dismissed. UI sounds are suppressed while visible so
    /// blips never play over commentary.
    /// </summary>
    event EventHandler<bool>? PlaybackVisibilityChanged;
}
