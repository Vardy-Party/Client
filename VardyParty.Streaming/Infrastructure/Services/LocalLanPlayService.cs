using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using VardyParty.LocalService.Client.Discovery;

namespace VardyParty.Streaming;

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
    IEnumerable<IPlaybackTransportPlugin> transportPlugins,
    ILogger<LocalLanPlayService> logger) : ILocalLanPlayService
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromSeconds(120);

    private readonly TimeSpan _m3u8CallTimeout =
        TimeSpan.FromSeconds(gamesApiSettings.Value?.M3U8CallTimeoutSeconds ?? 10);

    private string? _cachedBaseUrl;
    private DateTimeOffset _cachedBaseUrlAt = DateTimeOffset.MinValue;
    private string[] _cachedCapabilities = [];
    private DateTimeOffset _capabilitiesCachedAt = DateTimeOffset.MinValue;
    private string? _cachedServiceVersion;

    public HttpStatusCode? LastHealthStatus { get; private set; }

    public string? DiscoveredServiceVersion => _cachedServiceVersion;

    public async Task<bool> SupportsPlayStreamQueryAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCapabilitiesIfNeededAsync(cancellationToken);
        return SupportsPlayStreamQuery(_cachedCapabilities);
    }

    public async Task<string?> GetDiscoveredServiceVersionAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCapabilitiesIfNeededAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(_cachedServiceVersion))
            return _cachedServiceVersion;

        // Force a health probe even when capabilities were cached from UDP without serviceVersion.
        _capabilitiesCachedAt = DateTimeOffset.MinValue;
        await RefreshCapabilitiesIfNeededAsync(cancellationToken);
        return _cachedServiceVersion;
    }

    private static bool SupportsPlayStreamQuery(IEnumerable<string> capabilities) =>
        capabilities.Any(c => string.Equals(c, "play.stream", StringComparison.OrdinalIgnoreCase));

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = await ResolveServiceBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        if (await CheckHealthAsync(baseUrl, cancellationToken))
            return true;

        // If the service responded with an authentication or authorization failure (401/403),
        // it is running and reachable on this port. Retrying UDP discovery across the LAN is futile.
        if (LastHealthStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return false;

        InvalidateDiscoveryCache();
        baseUrl = await ResolveServiceBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        return await CheckHealthAsync(baseUrl, cancellationToken);
    }

    public Task<M3U8Response?> ResolveM3U8UrlAsync(string streamUrl, CancellationToken cancellationToken = default) =>
        ResolveM3U8UrlAsync(streamUrl, playerStreamName: null, resolutionStrategy: null, source: null, cancellationToken);

    public Task<M3U8Response?> ResolveM3U8UrlAsync(
        string streamUrl,
        string? playerStreamName,
        CancellationToken cancellationToken = default) =>
        ResolveM3U8UrlAsync(streamUrl, playerStreamName, resolutionStrategy: null, source: null, cancellationToken);

    public async Task<M3U8Response?> ResolveM3U8UrlAsync(
        string streamUrl,
        string? playerStreamName,
        string? resolutionStrategy,
        string? source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
            return null;

        var baseUrl = await ResolveServiceBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogWarning("[LocalLanPlay] Could not resolve local service endpoint for stream URL {Url}", streamUrl);
            return null;
        }

        await RefreshCapabilitiesIfNeededAsync(cancellationToken);
        // Catalog resolutionStrategy/source + transport plugins choose /mp vs /play — never URL host sniffing.
        // Playlist CTU rewrite stays on LocalService (/mp); client has no playlist body on this path.
        var useMp = ShouldUseMpEndpoint(resolutionStrategy, source, _cachedCapabilities);
        var effectiveStreamName = SupportsPlayStreamQuery(_cachedCapabilities)
            ? playerStreamName
            : null;
        if (!string.IsNullOrWhiteSpace(playerStreamName) && effectiveStreamName is null && !useMp)
        {
            logger.LogDebug(
                "[LocalLanPlay] Local service does not advertise play.stream; resolving without stream query param for {Url}",
                streamUrl);
        }

        var resolved = useMp
            ? await CallMpEndpointAsync(baseUrl, streamUrl, playerStreamName, cancellationToken)
            : await CallPlayEndpointAsync(baseUrl, streamUrl, effectiveStreamName, cancellationToken);
        if (resolved != null)
            return resolved;

        InvalidateDiscoveryCache();
        baseUrl = await ResolveServiceBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        await RefreshCapabilitiesIfNeededAsync(cancellationToken);
        useMp = ShouldUseMpEndpoint(resolutionStrategy, source, _cachedCapabilities);
        return useMp
            ? await CallMpEndpointAsync(baseUrl, streamUrl, playerStreamName, cancellationToken)
            : await CallPlayEndpointAsync(
                baseUrl,
                streamUrl,
                SupportsPlayStreamQuery(_cachedCapabilities) ? playerStreamName : null,
                cancellationToken);
    }

    /// <summary>
    /// Prefer matching <see cref="IPlaybackTransportPlugin"/> endpoint; require <c>mp.chrome</c>
    /// when the plugin (or fallback) selects <c>mp</c>.
    /// </summary>
    private bool ShouldUseMpEndpoint(
        string? resolutionStrategy,
        string? source,
        IEnumerable<string> capabilities)
    {
        var plugin = transportPlugins.FirstOrDefault(p => p.Matches(resolutionStrategy, source));
        if (plugin is not null)
        {
            var wantsMp = string.Equals(plugin.LocalServiceEndpoint, "mp", StringComparison.OrdinalIgnoreCase);
            if (!wantsMp)
                return false;

            return capabilities.Any(c =>
                string.Equals(c, "mp.chrome", StringComparison.OrdinalIgnoreCase));
        }

        return MpPageUrl.UseMpEndpoint(resolutionStrategy, source, capabilities);
    }

    private async Task RefreshCapabilitiesIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_capabilitiesCachedAt != DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow - _capabilitiesCachedAt < DiscoveryCacheTtl
            && _cachedCapabilities.Length > 0)
        {
            return;
        }

        var baseUrl = await ResolveServiceBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _cachedCapabilities = [];
            _capabilitiesCachedAt = DateTimeOffset.UtcNow;
            return;
        }

        _cachedCapabilities = await FetchCapabilitiesFromHealthAsync(baseUrl, cancellationToken);
        _capabilitiesCachedAt = DateTimeOffset.UtcNow;
    }

    private async Task<string[]> FetchCapabilitiesFromHealthAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var healthUrl = $"{baseUrl.TrimEnd('/')}/health";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var response = await httpClient.GetAsync(healthUrl, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    logger.LogWarning("[LocalLanPlay] Health check rejected with 401 Unauthorized reading capabilities from {Url}", healthUrl);
                }
                else if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    logger.LogWarning("[LocalLanPlay] Health check rejected with 403 Forbidden reading capabilities from {Url}", healthUrl);
                }
                return [];
            }

            var json = response.Content != null
                ? await response.Content.ReadAsStringAsync(cts.Token)
                : string.Empty;
            if (string.IsNullOrWhiteSpace(json))
                return [];

            using var doc = JsonDocument.Parse(json);
            ApplyHealthPayload(doc.RootElement);
            return _cachedCapabilities;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[LocalLanPlay] Failed to read capabilities from {Url}", healthUrl);
            return [];
        }
    }

    private void ApplyHealthPayload(JsonElement root)
    {
        _cachedCapabilities = ParseCapabilities(root);
        _cachedServiceVersion = ParseServiceVersion(root) ?? _cachedServiceVersion;
    }

    private static string[] ParseCapabilities(JsonElement root)
    {
        if (!root.TryGetProperty("capabilities", out var capabilitiesElement)
            || capabilitiesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return capabilitiesElement.EnumerateArray()
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    /// <summary>
    /// Reads package semver from <c>/health</c> (<c>version</c>) or UDP (<c>serviceVersion</c>).
    /// Discovery protocol <c>version</c> is an int and is ignored here.
    /// </summary>
    private static string? ParseServiceVersion(JsonElement root)
    {
        if (root.TryGetProperty("serviceVersion", out var serviceVersionEl)
            && serviceVersionEl.ValueKind == JsonValueKind.String)
        {
            var serviceVersion = serviceVersionEl.GetString();
            if (!string.IsNullOrWhiteSpace(serviceVersion))
                return serviceVersion.Trim();
        }

        if (root.TryGetProperty("version", out var versionEl)
            && versionEl.ValueKind == JsonValueKind.String)
        {
            var version = versionEl.GetString();
            if (!string.IsNullOrWhiteSpace(version))
                return version.Trim();
        }

        return null;
    }

    private async Task<bool> CheckHealthAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var healthUrl = $"{baseUrl.TrimEnd('/')}/health";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var response = await httpClient.GetAsync(healthUrl, cts.Token);
            LastHealthStatus = response.StatusCode;
            var isOk = response.IsSuccessStatusCode;
            if (!isOk)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    logger.LogWarning(
                        "[LocalLanPlay] Health check rejected with 401 Unauthorized for {Url}. Access token missing, expired, or invalid.",
                        healthUrl);
                }
                else if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    logger.LogWarning(
                        "[LocalLanPlay] Health check rejected with 403 Forbidden for {Url}. User lacks stream-viewer role.",
                        healthUrl);
                }
                else
                {
                    logger.LogInformation(
                        "[LocalLanPlay] Health check failed for {Url} with status {StatusCode}. Local service may be unavailable or misconfigured.",
                        healthUrl, response.StatusCode);
                }
            }
            else
            {
                logger.LogDebug("[LocalLanPlay] Health check successful for {Url}", healthUrl);
                var json = response.Content != null
                    ? await response.Content.ReadAsStringAsync(cts.Token)
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    using var doc = JsonDocument.Parse(json);
                    ApplyHealthPayload(doc.RootElement);
                    _capabilitiesCachedAt = DateTimeOffset.UtcNow;
                }
            }

            return isOk;
        }
        catch (Exception ex)
        {
            LastHealthStatus = null;
            logger.LogInformation(ex, "[LocalLanPlay] Health check request failed for {Url}. " +
                "Unable to connect to discovered local service endpoint.",
                healthUrl);
            return false;
        }
    }

    private static readonly TimeSpan MpCallTimeout = TimeSpan.FromSeconds(90);

    private static bool HasDiscoveredChips(M3U8Response? result) =>
        result?.Streams is { Count: > 0 };

    private async Task<M3U8Response?> CallMpEndpointAsync(
        string baseUrl,
        string streamUrl,
        string? playerStreamName,
        CancellationToken cancellationToken)
    {
        var url = $"{baseUrl.TrimEnd('/')}/mp";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(MpCallTimeout);

            var payload = JsonSerializer.Serialize(new
            {
                pageUrl = streamUrl,
                stream = string.IsNullOrWhiteSpace(playerStreamName) ? null : playerStreamName.Trim()
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            logger.LogInformation(
                "[LocalLanPlay] Resolving MP stream via POST {Url}{StreamSuffix}",
                url,
                string.IsNullOrWhiteSpace(playerStreamName) ? "" : $" (stream={playerStreamName.Trim()})");
            var response = await httpClient.PostAsync(url, content, cts.Token);
            var json = response.Content != null
                ? await response.Content.ReadAsStringAsync(cts.Token)
                : string.Empty;

            M3U8Response? result = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    result = JsonSerializer.Deserialize<M3U8Response>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException ex)
                {
                    logger.LogDebug(ex, "[LocalLanPlay] Failed to deserialize POST /mp response body as JSON");
                }
            }

            var chips = result?.Streams is { Count: > 0 }
                ? string.Join(", ", result.Streams)
                : "(none)";

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    logger.LogWarning(
                        "[LocalLanPlay] POST /mp rejected with 401 Unauthorized for {StreamUrl}. Local service requires a valid Auth0 access token.",
                        streamUrl);
                }
                else if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    logger.LogWarning(
                        "[LocalLanPlay] POST /mp rejected with 403 Forbidden for {StreamUrl}. User lacks required stream-viewer role.",
                        streamUrl);
                }
                else
                {
                    logger.LogInformation(
                        "[LocalLanPlay] POST /mp returned {StatusCode} for {StreamUrl}; chips={Chips}",
                        response.StatusCode, streamUrl, chips);
                }

                return HasDiscoveredChips(result) ? result : null;
            }

            if (result == null)
            {
                logger.LogWarning("[LocalLanPlay] POST /mp returned success but no body");
                return null;
            }

            if (string.IsNullOrWhiteSpace(result.Url))
            {
                logger.LogWarning(
                    "[LocalLanPlay] POST /mp returned success but no m3u8 URL; chips={Chips} selected={Selected}",
                    chips, result.SelectedStream ?? "(none)");
                return HasDiscoveredChips(result) ? result : null;
            }

            logger.LogInformation(
                "[LocalLanPlay] Resolved MP m3u8 via local service for {StreamUrl} selected={Selected} chips={Chips}",
                streamUrl, result.SelectedStream ?? "(none)", chips);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "[LocalLanPlay] Failed POST /mp via {BaseUrl} for {StreamUrl}",
                baseUrl, streamUrl);
            return null;
        }
    }

    private async Task<M3U8Response?> CallPlayEndpointAsync(
        string baseUrl,
        string streamUrl,
        string? playerStreamName,
        CancellationToken cancellationToken)
    {
        var url = $"{baseUrl.TrimEnd('/')}/play/{Uri.EscapeDataString(streamUrl)}";
        if (!string.IsNullOrWhiteSpace(playerStreamName))
        {
            url += $"?stream={Uri.EscapeDataString(playerStreamName.Trim())}";
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_m3u8CallTimeout);

            logger.LogDebug("[LocalLanPlay] Resolving stream via local service: {Url}", url);
            var response = await httpClient.GetAsync(url, cts.Token);
            var json = response.Content != null
                ? await response.Content.ReadAsStringAsync(cts.Token)
                : string.Empty;

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    logger.LogWarning(
                        "[LocalLanPlay] GET /play rejected with 401 Unauthorized for {StreamUrl}. Local service requires a valid Auth0 access token.",
                        streamUrl);
                }
                else if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    logger.LogWarning(
                        "[LocalLanPlay] GET /play rejected with 403 Forbidden for {StreamUrl}. User lacks required stream-viewer role.",
                        streamUrl);
                }
                else
                {
                    logger.LogWarning(
                        "[LocalLanPlay] GET /play returned {StatusCode} for {StreamUrl}: {Body}",
                        response.StatusCode, streamUrl, json);
                }

                return null;
            }

            M3U8Response? result = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    result = JsonSerializer.Deserialize<M3U8Response>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "[LocalLanPlay] Failed to deserialize GET /play response body: {Body}", json);
                }
            }

            if (result == null || string.IsNullOrWhiteSpace(result.Url))
            {
                logger.LogWarning("[LocalLanPlay] Local service returned success but no m3u8 URL in response body");
                return null;
            }

            logger.LogInformation("[LocalLanPlay] Resolved m3u8 via local service for {StreamUrl}", streamUrl);
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
                ApplyHealthPayload(root);
                _capabilitiesCachedAt = DateTimeOffset.UtcNow;
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
        _cachedCapabilities = [];
        _cachedServiceVersion = null;
        _capabilitiesCachedAt = DateTimeOffset.MinValue;
    }
}