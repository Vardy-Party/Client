namespace VardyParty.Health;

public enum StreamHealthStatus
{
    Unknown,
    Healthy,
    ManifestUnreachable,
    InvalidManifest,
    EmptyManifest,
    SegmentUnreachable
}
