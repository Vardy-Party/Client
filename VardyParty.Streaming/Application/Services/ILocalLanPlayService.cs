using VardyParty.Models;

namespace VardyParty.Streaming;

public interface ILocalLanPlayService
{
    Task<M3U8Response?> ResolveM3U8UrlAsync(string streamUrl, CancellationToken cancellationToken = default);

    Task<M3U8Response?> ResolveM3U8UrlAsync(
        string streamUrl,
        string? playerStreamName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the discovered local service supports <c>/play?stream=</c> (newer builds only).
    /// </summary>
    Task<bool> SupportsPlayStreamQueryAsync(CancellationToken cancellationToken = default);

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}