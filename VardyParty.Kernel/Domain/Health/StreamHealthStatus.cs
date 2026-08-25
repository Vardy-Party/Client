namespace VardyParty.Models;

public enum StreamHealthStatus
{
    Unknown,
    Healthy,
    ManifestUnreachable,
    InvalidManifest,
    EmptyManifest,
    SegmentUnreachable
}
