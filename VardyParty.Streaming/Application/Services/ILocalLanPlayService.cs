using VardyParty.Kernel;

namespace VardyParty.Streaming;

public interface ILocalLanPlayService
{
    Task<M3U8Response?> ResolveM3U8UrlAsync(string streamUrl, CancellationToken cancellationToken = default);

    Task<M3U8Response?> ResolveM3U8UrlAsync(
        string streamUrl,
        string? playerStreamName,
        CancellationToken cancellationToken = default);

    Task<M3U8Response?> ResolveM3U8UrlAsync(
        string streamUrl,
        string? playerStreamName,
        string? resolutionStrategy,
        string? source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the discovered local service supports <c>/play?stream=</c> (newer builds only).
    /// </summary>
    Task<bool> SupportsPlayStreamQueryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the discovered local service can share this machine as a compute host
    /// (<c>compute.host</c> in cached <c>/health</c> capabilities, or a non-404
    /// <c>/compute/host/status</c> probe for older builds that already have the route).
    /// </summary>
    Task<bool> SupportsComputeHostAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Package version reported by the discovered LocalService (<c>/health</c> <c>version</c>),
    /// or null if unknown / unavailable.
    /// </summary>
    string? DiscoveredServiceVersion { get; }

    /// <summary>
    /// Refresh discovery/health and return the LocalService package version when available.
    /// </summary>
    Task<string?> GetDiscoveredServiceVersionAsync(CancellationToken cancellationToken = default);

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>True when this device is paired to a remote compute host.</summary>
    bool UsesRemoteCompute { get; }

    /// <summary>
    /// User-facing fault from the last resolve that used the remote host.
    /// Null when that resolve did not use the relay, or it succeeded.
    /// </summary>
    string? LastRemoteComputeError => null;

    /// <summary>
    /// Base URL of the discovered LAN resolver, using the same discovery cache
    /// as <c>/play</c> and <c>/mp</c>. Null when nothing is on the network.
    /// </summary>
    Task<string?> GetServiceBaseUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The HTTP status code returned by the most recent health check, or null if no check was attempted or connection failed.
    /// </summary>
    System.Net.HttpStatusCode? LastHealthStatus => null;
}
