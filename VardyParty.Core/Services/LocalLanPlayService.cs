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
                logger.LogWarning("[LocalLanPlay] Health check failed for {Url} with status {StatusCode}",
                    healthUrl, response.StatusCode);
            }

            return isOk;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LocalLanPlay] Health check request failed for {Url}", healthUrl);
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

            logger.LogInformation("[LocalLanPlay] Resolving stream via local service: {Url}", url);
            var response = await httpClient.GetAsync(url, cts.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            return JsonSerializer.Deserialize<M3U8Response>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LocalLanPlay] Failed to resolve m3u8 via local service endpoint {BaseUrl}", baseUrl);
            return null;
        }
    }

    private async Task<string?> ResolveServiceBaseUrlAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedBaseUrl) && DateTimeOffset.UtcNow - _cachedBaseUrlAt < DiscoveryCacheTtl)
            return _cachedBaseUrl;

        var discovered = await DiscoverViaUdpAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(discovered))
        {
            _cachedBaseUrl = discovered.TrimEnd('/');
            _cachedBaseUrlAt = DateTimeOffset.UtcNow;
        }

        return _cachedBaseUrl;
    }

    private async Task<string?> DiscoverViaUdpAsync(CancellationToken cancellationToken)
    {
        try
        {
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
                logger.LogInformation("[LocalLanPlay] Discovered local service endpoint: {Endpoint}", discoveredUrl);
                return discoveredUrl;
            }

            logger.LogWarning("[LocalLanPlay] UDP discovery timed out across ports: {Ports}",
                string.Join(",", probePorts));
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LocalLanPlay] UDP discovery failed");
            return null;
        }
    }

    private void InvalidateDiscoveryCache()
    {
        _cachedBaseUrl = null;
        _cachedBaseUrlAt = DateTimeOffset.MinValue;
    }
}