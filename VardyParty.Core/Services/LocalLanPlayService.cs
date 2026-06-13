using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Configuration;
using VardyParty.LocalService.Client.Discovery;
using VardyParty.Models;

namespace VardyParty.Services;

/// <summary>
/// Client-side Local LAN Play Service discovery and resolution.
/// 
/// This service discovers and communicates with a separate VardyParty.LocalService instance
/// running on the local network. It uses UDP broadcast discovery to find the service and then
/// makes HTTP requests to resolve M3U8 stream URLs.
/// 
/// NETWORK ARCHITECTURE:
/// - Each machine must run VardyParty.LocalService (separate application/service) to be discoverable
/// - Clients (phone, TV, PC) use this service to discover the local service on the same LAN
/// - Discovery uses ephemeral UDP sockets (no permanent port bindings)
/// - Discovery only works on local networks; cross-WAN discovery is not supported
/// 
/// CROSS-MACHINE BEHAVIOR:
/// - On the same LAN: Clients broadcast discover the service successfully (if service is running)
/// - On different LANs/Networks: Discovery will fail; VardyParty.LocalService must be installed and running on each machine
/// - The Local Service is NOT automatically installed with the client
/// 
/// FAILURE HANDLING:
/// - Discovery timeouts and failures are logged for troubleshooting
/// - The service falls back gracefully if discovery fails
/// - ILocalLanServiceAvailabilityMonitor provides UI warnings when service is unavailable
/// </summary>
public class LocalLanPlayService(
    HttpClient httpClient,
    IOptions<GamesApiSettings> gamesApiSettings,
    ILogger<LocalLanPlayService> logger) : ILocalLanPlayService
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromSeconds(120);

    private readonly TimeSpan _m3u8CallTimeout =
        TimeSpan.FromSeconds(gamesApiSettings.Value?.M3U8CallTimeoutSeconds ?? 10);

    private string? _cachedBaseUrl;
    private DateTimeOffset _cachedBaseUrlAt = DateTimeOffset.MinValue;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = await ResolveServiceBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        if (await CheckHealthAsync(baseUrl, cancellationToken))
            return true;

        InvalidateDiscoveryCache();
        baseUrl = await ResolveServiceBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        return await CheckHealthAsync(baseUrl, cancellationToken);
    }

    public async Task<M3U8Response?> ResolveM3U8UrlAsync(string streamUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
            return null;

        var baseUrl = await ResolveServiceBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogWarning("[LocalLanPlay] Could not resolve local service endpoint for stream URL {Url}", streamUrl);
            return null;
        }

        var resolved = await CallPlayEndpointAsync(baseUrl, streamUrl, cancellationToken);
        if (resolved != null)
            return resolved;

        InvalidateDiscoveryCache();
        baseUrl = await ResolveServiceBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        return await CallPlayEndpointAsync(baseUrl, streamUrl, cancellationToken);
    }

    private async Task<bool> CheckHealthAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var healthUrl = $"{baseUrl.TrimEnd('/')}/health";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var response = await httpClient.GetAsync(healthUrl, cts.Token);
            var isOk = response.IsSuccessStatusCode;
            if (!isOk)
            {
                logger.LogInformation("[LocalLanPlay] Health check failed for {Url} with status {StatusCode}. " +
                    "Local service may be unavailable or misconfigured.",
                    healthUrl, response.StatusCode);
            }
            else
            {
                logger.LogDebug("[LocalLanPlay] Health check successful for {Url}", healthUrl);
            }

            return isOk;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "[LocalLanPlay] Health check request failed for {Url}. " +
                "Unable to connect to discovered local service endpoint.",
                healthUrl);
            return false;
        }
    }

    private async Task<M3U8Response?> CallPlayEndpointAsync(string baseUrl, string streamUrl, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl.TrimEnd('/')}/play/{Uri.EscapeDataString(streamUrl)}";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_m3u8CallTimeout);

            logger.LogDebug("[LocalLanPlay] Resolving stream via local service: {Url}", url);
            var response = await httpClient.GetAsync(url, cts.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            var result = JsonSerializer.Deserialize<M3U8Response>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            logger.LogDebug("[LocalLanPlay] Successfully resolved m3u8 via local service");
            return result;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "[LocalLanPlay] Failed to resolve m3u8 via local service endpoint {BaseUrl}. " +
                "Stream URL may be inaccessible or service may be temporarily unavailable.",
                baseUrl);
            return null;
        }
    }

    private async Task<string?> ResolveServiceBaseUrlAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedBaseUrl) && DateTimeOffset.UtcNow - _cachedBaseUrlAt < DiscoveryCacheTtl)
        {
            logger.LogDebug("[LocalLanPlay] Using cached service endpoint: {Endpoint} (expires in {TTL}s)",
                _cachedBaseUrl, (DiscoveryCacheTtl - (DateTimeOffset.UtcNow - _cachedBaseUrlAt)).TotalSeconds);
            return _cachedBaseUrl;
        }

        logger.LogDebug("[LocalLanPlay] Cache miss or expired, performing fresh discovery...");
        var discovered = await DiscoverViaUdpAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(discovered))
        {
            _cachedBaseUrl = discovered.TrimEnd('/');
            _cachedBaseUrlAt = DateTimeOffset.UtcNow;
            logger.LogInformation("[LocalLanPlay] Cached discovered service endpoint: {Endpoint} (TTL: {TTL}s)",
                _cachedBaseUrl, DiscoveryCacheTtl.TotalSeconds);
        }

        return _cachedBaseUrl;
    }

    private async Task<string?> DiscoverViaUdpAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogDebug("[LocalLanPlay] Starting UDP discovery for local service...");

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var probePayload = JsonSerializer.Serialize(new
            {
                type = DiscoveryProtocol.ProbeType,
                version = DiscoveryProtocol.Version
            });

            var bytes = Encoding.UTF8.GetBytes(probePayload);
            var probePorts = DiscoveryProtocol.DiscoveryPorts;
            logger.LogDebug("[LocalLanPlay] Sending discovery probes to ports: {Ports}", string.Join(",", probePorts));

            foreach (var port in probePorts)
            {
                try
                {
                    var remote = new IPEndPoint(IPAddress.Broadcast, port);
                    await udp.SendAsync(bytes, bytes.Length, remote);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[LocalLanPlay] Failed to send discovery probe to UDP port {Port}", port);
                }
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DiscoveryTimeout);

            while (!timeoutCts.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    var receiveTask = udp.ReceiveAsync(timeoutCts.Token).AsTask();
                    received = await receiveTask;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(received.Buffer);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement) ||
                    !string.Equals(typeElement.GetString(), DiscoveryProtocol.ResponseType, StringComparison.Ordinal))
                {
                    logger.LogDebug("[LocalLanPlay] Ignored UDP discovery response with unexpected type from {Endpoint}",
                        received.RemoteEndPoint);
                    continue;
                }

                if (!root.TryGetProperty("httpPort", out var portElement) || !portElement.TryGetInt32(out var httpPort) ||
                    httpPort <= 0)
                {
                    logger.LogDebug("[LocalLanPlay] Ignored UDP discovery response without valid httpPort from {Endpoint}",
                        received.RemoteEndPoint);
                    continue;
                }

                var host = received.RemoteEndPoint.Address.ToString();
                var discoveredUrl = $"http://{host}:{httpPort}";
                logger.LogInformation("[LocalLanPlay] Successfully discovered local service endpoint: {Endpoint}", discoveredUrl);
                return discoveredUrl;
            }

            logger.LogInformation("[LocalLanPlay] UDP discovery timed out after {Timeout}ms across ports {Ports}. " +
                "Ensure VardyParty.LocalService is running on a machine on your local network.",
                DiscoveryTimeout.TotalMilliseconds, string.Join(",", probePorts));
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LocalLanPlay] UDP discovery failed with exception. Verify VardyParty.LocalService is installed and running on your network.");
            return null;
        }
    }

    private void InvalidateDiscoveryCache()
    {
        _cachedBaseUrl = null;
        _cachedBaseUrlAt = DateTimeOffset.MinValue;
    }
}