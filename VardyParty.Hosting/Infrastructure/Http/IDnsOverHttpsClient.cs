using System.Net;

namespace VardyParty.Hosting;

/// <summary>DNS-over-HTTPS lookup (Cloudflare or test double).</summary>
public interface IDnsOverHttpsClient
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default);
}
