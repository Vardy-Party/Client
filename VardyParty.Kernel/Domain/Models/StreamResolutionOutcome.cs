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

    public PlaybackResult? PlaybackResult { get; set; }
}
