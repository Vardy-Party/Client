namespace VardyParty.Configuration;

public class StreamHealthSettings
{
    public required int ManifestTimeoutSeconds { get; set; }
    public required int SegmentTimeoutSeconds { get; set; }
    public required int MaxParallelStreams { get; set; }
}
