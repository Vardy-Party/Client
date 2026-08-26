namespace VardyParty.Kernel;

public class StreamResolutionOutcome
{
    public bool UserClosed { get; set; }
    public bool NoWorkingStreams { get; set; }
    public PlaybackResult? PlaybackResult { get; set; }
}
