using VardyParty.Models;

namespace VardyParty.Services;

public interface ILocalLanPlayService
{
    Task<M3U8Response?> ResolveM3U8UrlAsync(string streamUrl, CancellationToken cancellationToken = default);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}