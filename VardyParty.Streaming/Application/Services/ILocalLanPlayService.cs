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

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The HTTP status code returned by the most recent health check, or null if no check was attempted or connection failed.
    /// </summary>
    System.Net.HttpStatusCode? LastHealthStatus => null;
}
