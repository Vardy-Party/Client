using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VardyParty.Ports;

namespace VardyParty.Hosting;

/// <summary>
/// Resolves host names for internet <see cref="HttpClient"/> (health probes,
/// CDN fetches, API). When the DoH preference is on, queries Cloudflare first
/// — same order as <see cref="PhonePathDns"/> — then system DNS. A "successful"
/// carrier/hijack system answer must not skip DoH; that was preventing CDN
/// health checks from ever using 1.1.1.1. When DoH is off, system DNS only.
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

        var dohEnabled = _preferences.LoadDnsOverHttpsFallbackEnabled();
        PhonePathDns.Result result;
        try
        {
            result = await PhonePathDns.ResolveAsync(
                host,
                dohEnabled,
                doh: async (h, ct) =>
                {
                    _logger?.LogInformation("[DoH] Preferring Cloudflare 1.1.1.1 for {Host}", h);
                    return await _doh.ResolveAsync(h, ct).ConfigureAwait(false);
                },
                system: async (h, ct) =>
                {
                    try
                    {
                        return await Dns.GetHostAddressesAsync(h, ct).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        throw;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SocketException)
        {
            // Dual A/AAAA DoH can surface AggregateException; connect path expects SocketException.
            _logger?.LogWarning(ex, "[DoH] Lookup failed for {Host}", host);
            throw new SocketException((int)SocketError.HostNotFound);
        }

        if (result.Addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        if (result.Via == PhonePathDns.Source.System && dohEnabled)
        {
            _logger?.LogInformation(
                "[DoH] Cloudflare missed {Host}; using system DNS ({Count} address(es))",
                host,
                result.Addresses.Length);
        }

        Remember(host, result.Addresses);
        return result.Addresses;
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
