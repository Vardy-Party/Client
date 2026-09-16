namespace VardyParty.Kernel;

public class StreamResolutionOutcome
{
    public bool UserClosed { get; set; }
    public bool NoWorkingStreams { get; set; }

    /// <summary>
    /// The orchestrator could not begin because the previous resolution
    /// session was still holding the start gate when the bounded wait ran
    /// out. Nothing was resolved; hosts must surface this instead of
    /// treating it as a silent no-op.
    /// </summary>
    public bool StartRefused { get; set; }

    /// <summary>
    /// Local LAN play service is not running or unreachable on the network.
    /// Surfaced explicitly so hosts do not confuse it with catalog stream failures.
    /// </summary>
    public bool LocalServiceUnavailable { get; set; }

    public PlaybackResult? PlaybackResult { get; set; }
}
