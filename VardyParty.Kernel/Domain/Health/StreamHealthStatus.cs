namespace VardyParty.Kernel;

public enum StreamHealthStatus
{
    Unknown,
    Healthy,
    ManifestUnreachable,
    InvalidManifest,
    EmptyManifest,
    SegmentUnreachable
}
