using System.Net;

namespace VardyParty.Hosting;

/// <summary>
/// CONNECT targets on the paired phone. Carrier DNS on 4G often returns
/// a hijack or search-suffix answer, which would skip DoH if we tried
/// system DNS first. When DoH is on, query it first and only then system DNS.
/// </summary>
public static class PhonePathDns
{
    public enum Source
    {
        Literal,
        Doh,
        System
    }

    public readonly record struct Result(IPAddress[] Addresses, Source Via);

    public static async Task<Result> ResolveAsync(
        string host,
        bool dohEnabled,
        Func<string, CancellationToken, Task<IPAddress[]>> doh,
        Func<string, CancellationToken, Task<IPAddress[]>> system,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(doh);
        ArgumentNullException.ThrowIfNull(system);

        if (IPAddress.TryParse(host, out var literal))
        {
            return new Result([literal], Source.Literal);
        }

        if (dohEnabled)
        {
            try
            {
                var addresses = await doh(host, cancellationToken).ConfigureAwait(false);
                if (addresses.Length > 0)
                {
                    return new Result(addresses, Source.Doh);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // System DNS is the fallback.
            }
        }

        var systemAddresses = await system(host, cancellationToken).ConfigureAwait(false);
        return new Result(systemAddresses, Source.System);
    }
}
