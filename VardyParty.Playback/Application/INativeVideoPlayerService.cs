using VardyParty.Kernel;
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

    /// <summary>
    /// Result of the session that just hid. Set before
    /// <see cref="PlaybackVisibilityChanged"/> is raised with false, so the
    /// host can tell a source error from the viewer leaving.
    /// </summary>
    PlaybackResult? LastPlaybackResult { get; }
}
