using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VardyParty.Ports;

namespace VardyParty.Hosting;

/// <summary>
/// Resolves host names with system DNS first; when that fails (or returns no
/// addresses) and the DoH fallback preference is on, queries Cloudflare DoH.
/// </summary>
public sealed class SystemThenDohHostNameResolver : IHostNameResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IDnsPreferencesStore _preferences;
    private readonly IDnsOverHttpsClient _doh;
    private readonly ILogger<SystemThenDohHostNameResolver>? _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SystemThenDohHostNameResolver(
        IDnsPreferencesStore preferences,
        IDnsOverHttpsClient doh,
        ILogger<SystemThenDohHostNameResolver>? logger = null)
    {
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _doh = doh ?? throw new ArgumentNullException(nameof(doh));
        _logger = logger;
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (IPAddress.TryParse(host, out var literal))
            return [literal];

        if (_cache.TryGetValue(host, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow)
            return cached.Addresses;

        SocketException? systemFailure = null;
        IPAddress[] systemAddresses = [];
        try
        {
            systemAddresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            systemFailure = ex;
        }

        if (systemAddresses.Length > 0)
        {
            Remember(host, systemAddresses);
            return systemAddresses;
        }

        if (!_preferences.LoadDnsOverHttpsFallbackEnabled())
        {
            throw systemFailure ?? new SocketException((int)SocketError.HostNotFound);
        }

        _logger?.LogInformation(
            "[DoH] System DNS failed for {Host} ({Error}); trying Cloudflare 1.1.1.1",
            host,
            systemFailure?.Message ?? "no addresses");

        var dohAddresses = await _doh.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        if (dohAddresses.Length == 0)
        {
            throw systemFailure ?? new SocketException((int)SocketError.HostNotFound);
        }

        Remember(host, dohAddresses);
        return dohAddresses;
    }

    private void Remember(string host, IPAddress[] addresses)
    {
        _cache[host] = new CacheEntry(addresses, DateTimeOffset.UtcNow.Add(CacheTtl));
    }

    private readonly record struct CacheEntry(IPAddress[] Addresses, DateTimeOffset ExpiresUtc);
}

/// <summary>Looks up A/AAAA records for a host name.</summary>
public interface IHostNameResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default);
}
