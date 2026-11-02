namespace VardyParty.Kernel;

public enum StreamHealthStatus
{
    Unknown,
    Healthy,
    ManifestUnreachable,
    InvalidManifest,
    EmptyManifest,

    /// <summary>
    /// A segment answered with an HTTP error, or its bytes are not video (decoy
    /// image, HTML). Dead. A segment we could not CONNECT to is
    /// <see cref="CdnUnreachable"/> instead.
    /// </summary>
    SegmentUnreachable,

    /// <summary>The manifest answered HTTP 404 or 410: the playlist is gone. Dead.</summary>
    ManifestNotFound,

    /// <summary>
    /// The CDN host name does not resolve (NAME_NOT_RESOLVED). Kept dead by
    /// choice: see <see cref="CdnReachabilityPolicy"/>.
    /// </summary>
    HostNotResolved,

    /// <summary>
    /// Could not reach the CDN: connect timeout, connection refused or reset,
    /// TLS failure, on the manifest or a segment. Never dead: the row is
    /// deferred and rechecked (<see cref="CdnReachabilityPolicy"/>).
    /// </summary>
    CdnUnreachable
}
