namespace VardyParty.Playback;

/// <summary>
/// Shared playback session lifecycle. OS engines only report facts; Core owns this state.
/// </summary>
public enum PlaybackSessionState
{
    Idle,
    Starting,
    Playing,
    Buffering,
    Switching,
    Failed,
    Closed
}
