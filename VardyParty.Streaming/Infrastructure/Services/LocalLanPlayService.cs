using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using VardyParty.LocalService.Client.Discovery;
using VardyParty.Ports;

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
    ILogger<LocalLanPlayService> logger,
    IDnsPreferencesStore? dnsPreferences = null,
    IDnsOverHttpsEndpoint? dnsEndpoint = null,
    IRemoteComputePlay? remoteCompute = null,
    LocalServiceConnection? connection = null) : ILocalLanPlayService
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromSeconds(120);
    /// <summary>
    /// Only throttles UDP probes. Queued work no longer fails during it:
    /// <see cref="ResolveServiceBaseUrlAsync"/> keeps the last good endpoint and
    /// retries discovery (<see cref="DiscoveryRetryDelays"/>) before it reports
    /// the service unavailable.
    /// </summary>
    private static readonly TimeSpan DiscoveryFailureCacheTtl = TimeSpan.FromSeconds(1);

    /// <summary>The waits between discovery attempts when no endpoint has worked yet.</summary>
    internal static readonly TimeSpan[] DiscoveryRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5)];

    /// <summary>
    /// How many back-to-back connection failures (no HTTP response at all) it
    /// takes before the known-good endpoint is forgotten. Any HTTP response
    /// resets the count. A slow or failed <c>/play</c> never counts.
    /// </summary>
    internal const int ConnectionFailuresBeforeInvalidate = 3;

    /// <summary>
    /// A stream whose <c>/play</c> hit our timeout gets a longer timeout on the
    /// next attempt for this long.
    /// </summary>
    private static readonly TimeSpan SlowStreamMemory = TimeSpan.FromMinutes(10);

    // Never below LocalService's final wait plus a margin (35s + 30s = 65s, see
    // LocalServicePlayBudget). The configured 20s cut the service off while it
    // was still resolving. A larger configured value wins.
    private readonly TimeSpan _m3u8CallTimeout = LocalServicePlayBudget.BaseTimeout(
        gamesApiSettings.Value is { } settings ? TimeSpan.FromSeconds(settings.M3U8CallTimeoutSeconds) : null);

    private readonly LocalServiceEndpointCache _endpoints = new(DiscoveryCacheTtl, DiscoveryFailureCacheTtl);

    internal LocalServiceEndpointCache EndpointsForTests => _endpoints;

    /// <summary>Test seam for the discovery retry waits.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    private readonly object _resolveGate = new();
    private readonly Dictionary<string, SharedResolve> _resolveFlights = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _slowStreams = new(StringComparer.Ordinal);
    private int _consecutiveConnectionFailures;

    internal int ConsecutiveConnectionFailuresForTests => Volatile.Read(ref _consecutiveConnectionFailures);

    /// <summary>
    /// Test seam so a failed /play can invalidate discovery without a real UDP probe.
    /// </summary>
    internal Func<CancellationToken, Task<string?>>? DiscoverOverrideForTests { get; set; }
    private string[] _cachedCapabilities = [];
    private DateTimeOffset _capabilitiesCachedAt = DateTimeOffset.MinValue;
    private string? _cachedServiceVersion;
    private readonly object _mpHarvestGate = new();
    private MpHarvestSession? _mpHarvest;
    private string? _mpHarvestPage;

    /// <summary>
    /// After <c>/mp rejected: V2/MP scrape disabled</c>, MP rows on that
    /// service are answered from memory for this long (no POST), as
    /// <see cref="LocalServiceUnavailableReason.MpDisabled"/>. The client never
    /// tries to switch V2/MP back on.
    /// </summary>
    internal static readonly TimeSpan MpDisabledMemory = TimeSpan.FromSeconds(60);

    private readonly object _mpDisabledGate = new();
    private string? _mpDisabledBaseUrl;
    private DateTimeOffset _mpDisabledAt;

    /// <summary>Test seam: clock for <see cref="MpDisabledMemory"/>.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = static () => DateTimeOffset.UtcNow;

    public HttpStatusCode? LastHealthStatus { get; private set; }

    public string? DiscoveredServiceVersion => _cachedServiceVersion;

    public async Task<bool> SupportsPlayStreamQueryAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCapabilitiesIfNeededAsync(cancellationToken);
        return SupportsPlayStreamQuery(_cachedCapabilities);
    }

    public async Task<bool> SupportsComputeHostAsync(CancellationToken cancellationToken = default)
    {
        await RefreshCapabilitiesIfNeededAsync(cancellationToken);
        if (HasComputeHostCapability(_cachedCapabilities))
            return true;

        var baseUrl = await ResolveServiceBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        return await ProbeComputeHostStatusAsync(baseUrl, cancellationToken);
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

    private static bool HasComputeHostCapability(IEnumerable<string> capabilities) =>
        capabilities.Any(c => string.Equals(c, "compute.host", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Older hosts have <c>/compute/host/*</c> but do not advertise <c>compute.host</c>.
    /// Any non-404 status means the route exists.
    /// </summary>
    private async Task<bool> ProbeComputeHostStatusAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, CombineLocalPath(baseUrl, "/compute/host/status"));
            var response = await httpClient.SendAsync(request, cts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogDebug("[LocalLanPlay] Compute host status probe returned 404; sharing is not advertised");
                return false;
            }

            logger.LogDebug("[LocalLanPlay] Compute host status probe returned {StatusCode}", (int)response.StatusCode);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[LocalLanPlay] Compute host status probe failed");
            return false;
        }
    }

    private static string CombineLocalPath(string baseUrl, string path) =>
        $"{baseUrl.TrimEnd('/')}{path}";

    private bool _usedRemote;

    public bool UsesRemoteCompute => remoteCompute?.IsPaired == true;

    public string? LastRemoteComputeError =>
        _usedRemote ? remoteCompute?.LastFault?.Display : null;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = await ResolveServiceBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        if (await CheckHealthAsync(baseUrl, cancellationToken))
            return true;

        // If the service responded with an authentication or authorization failure (401/403),
        // it is running and reachable on this port. Retrying UDP discovery across the LAN is futile.
        cancellationToken.ThrowIfCancellationRequested();
        if (LastHealthStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return false;

        // Don't forget the known-good endpoint after one failed probe. Ask the LAN
        // whether the service is now somewhere else. Only a reply with a different
        // endpoint replaces it; otherwise the repeated-failure rule in
        // RecordConnectionFailure decides.
        var rediscovered = await ProbeForMovedServiceAsync(baseUrl, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rediscovered))
            return false;

        return await CheckHealthAsync(rediscovered, cancellationToken);
    }

    /// <summary>
    /// A direct UDP probe that bypasses the cache. Returns a newly discovered
    /// endpoint only when it differs from <paramref name="currentBaseUrl"/>.
    /// </summary>
    private async Task<string?> ProbeForMovedServiceAsync(string currentBaseUrl, CancellationToken cancellationToken)
    {
        var discovered = DiscoverOverrideForTests is { } discover
            ? await discover(cancellationToken).ConfigureAwait(false)
            : await DiscoverViaUdpAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(discovered))
            return null;

        var normalized = discovered.TrimEnd('/');
        if (string.Equals(normalized, currentBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return null;

        logger.LogInformation("[LocalLanPlay] Local service moved from {Old} to {New}", currentBaseUrl, normalized);
        _endpoints.Remember(normalized, DateTimeOffset.UtcNow);
        Interlocked.Exchange(ref _consecutiveConnectionFailures, 0);
        return normalized;
    }

    public Task<M3U8Response?> ResolveM3U8UrlAsync(string streamUrl, CancellationToken cancellationToken = default) =>
        ResolveM3U8UrlAsync(streamUrl, playerStreamName: null, resolutionStrategy: null, source: null, cancellationToken);

    public Task<M3U8Response?> ResolveM3U8UrlAsync(
        string streamUrl,
        string? playerStreamName,
        CancellationToken cancellationToken = default) =>
        ResolveM3U8UrlAsync(streamUrl, playerStreamName, resolutionStrategy: null, source: null, cancellationToken);

    /// <summary>
    /// Resolves one stream through the local service. It returns <c>null</c>
    /// only when the service answered and the stream has no m3u8 (HTTP error
    /// status or an empty body): a stream failure. It throws
    /// <see cref="LocalServiceUnavailableException"/> for infrastructure
    /// failures (no endpoint, our timeout, a connection failure), so callers can
    /// queue or retry instead of dropping the stream.
    /// <para>
    /// Single-flight per stream: concurrent callers for the same row share one
    /// <c>/play</c>. A caller's cancel aborts that call only when no other caller
    /// is still waiting.
    /// </para>
    /// </summary>
    public async Task<M3U8Response?> ResolveM3U8UrlAsync(
        string streamUrl,
        string? playerStreamName,
        string? resolutionStrategy,
        string? source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamUrl))
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        var key = BuildResolveKey(streamUrl, playerStreamName, resolutionStrategy, source);
        SharedResolve flight;
        var owner = false;
        lock (_resolveGate)
        {
            // A flight whose last waiter already cancelled is about to fail with a
            // cancel; a new caller starts its own instead of inheriting that cancel.
            if (_resolveFlights.TryGetValue(key, out var existing)
                && !existing.Completion.Task.IsCompleted
                && !existing.Cancellation.IsCancellationRequested)
            {
                existing.Waiters++;
                flight = existing;
                logger.LogDebug("[LocalLanPlay] Joining in-flight resolve for {Url}", streamUrl);
            }
            else
            {
                flight = new SharedResolve();
                _resolveFlights[key] = flight;
                owner = true;
            }
        }

        if (owner)
            _ = RunSharedResolveAsync(key, flight, streamUrl, playerStreamName, resolutionStrategy, source);

        try
        {
            return await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_resolveGate)
            {
                if (flight.Waiters > 0)
                    flight.Waiters--;
                if (flight.Waiters == 0)
                    flight.Cancellation.Cancel();
            }

            throw;
        }
    }

    private static string BuildResolveKey(string streamUrl, string? playerStreamName, string? resolutionStrategy, string? source)
        => string.Join('\u001f', streamUrl.Trim(), playerStreamName?.Trim() ?? string.Empty,
            resolutionStrategy?.Trim() ?? string.Empty, source?.Trim() ?? string.Empty);

    private async Task RunSharedResolveAsync(
        string key,
        SharedResolve flight,
        string streamUrl,
        string? playerStreamName,
        string? resolutionStrategy,
        string? source)
    {
        try
        {
            var result = await ResolveM3U8UrlCoreAsync(
                key, streamUrl, playerStreamName, resolutionStrategy, source, flight.Cancellation.Token).ConfigureAwait(false);
            flight.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException ex)
        {
            flight.Completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            flight.Completion.TrySetException(ex);
        }
        finally
        {
            lock (_resolveGate)
            {
                if (_resolveFlights.TryGetValue(key, out var current) && ReferenceEquals(current, flight))
                    _resolveFlights.Remove(key);
            }

            // The CTS has no timer, so it is not disposed: a late waiter may still
            // call Cancel() on it, which must not throw ObjectDisposedException.
        }
    }

    private async Task<M3U8Response?> ResolveM3U8UrlCoreAsync(
        string key,
        string streamUrl,
        string? playerStreamName,
        string? resolutionStrategy,
        string? source,
        CancellationToken cancellationToken)
    {
        _usedRemote = false;
        var baseUrl = await ResolveServiceBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            var remote = await ResolveViaRemoteAsync(streamUrl, playerStreamName, resolutionStrategy, source, cancellationToken);
            if (remote is not null || remoteCompute?.IsPaired == true)
            {
                return remote;
            }

            logger.LogWarning("[LocalLanPlay] Could not resolve local service endpoint for stream URL {Url}", streamUrl);
            throw new LocalServiceUnavailableException(
                LocalServiceUnavailableReason.EndpointUnknown,
                "Local service endpoint not discovered");
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

        // One call. A null means the service ANSWERED with no m3u8, so that's the
        // stream's verdict. It used to invalidate discovery and /play a second
        // time; every dead stream then caused a re-discovery
        // (16:51:43-16:53:03 UK time on 31 Oct 2026) and a double /play
        // (techydeals at 16:49:54 and 16:50:14).
        return useMp
            ? await CallMpEndpointAsync(baseUrl, streamUrl, playerStreamName, cancellationToken)
            : await CallPlayEndpointAsync(baseUrl, streamUrl, effectiveStreamName, key, cancellationToken);
    }

    /// <summary>Test seam: mark a resolve key slow, as our own <c>/play</c> timeout does.</summary>
    internal void MarkSlowForTests(string key) => _slowStreams[key] = DateTimeOffset.UtcNow;

    /// <summary>
    /// The <c>/play</c> timeout for a resolve key: the base (at least 65s), or
    /// double it (capped at 120s) for a stream that hit our timeout in the last
    /// 10 minutes.
    /// </summary>
    internal TimeSpan PlayTimeoutFor(string key)
    {
        if (_slowStreams.TryGetValue(key, out var markedAt))
        {
            if (DateTimeOffset.UtcNow - markedAt < SlowStreamMemory)
            {
                return LocalServicePlayBudget.ExtendedTimeout(_m3u8CallTimeout);
            }

            _slowStreams.TryRemove(key, out _);
        }

        return _m3u8CallTimeout;
    }

    /// <summary>Any HTTP response from the service proves the endpoint is alive.</summary>
    private void RecordServiceResponded() => Interlocked.Exchange(ref _consecutiveConnectionFailures, 0);

    /// <summary>
    /// A call got no HTTP response at all. After
    /// <see cref="ConnectionFailuresBeforeInvalidate"/> in a row, forget the
    /// endpoint so the next call rediscovers it.
    /// </summary>
    private void RecordConnectionFailure(string baseUrl, Exception? exception)
    {
        var failures = Interlocked.Increment(ref _consecutiveConnectionFailures);
        if (failures < ConnectionFailuresBeforeInvalidate)
        {
            logger.LogInformation(
                "[LocalLanPlay] Connection to local service {BaseUrl} failed ({Count}/{Max}); keeping endpoint",
                baseUrl, failures, ConnectionFailuresBeforeInvalidate);
            return;
        }

        logger.LogWarning(exception,
            "[LocalLanPlay] {Count} consecutive connection failures to {BaseUrl}; invalidating endpoint",
            failures, baseUrl);
        Interlocked.Exchange(ref _consecutiveConnectionFailures, 0);
        InvalidateDiscoveryCache();
    }

    /// <summary>
    /// No HTTP response at all: refused, reset, unreachable, DNS. An
    /// <see cref="HttpRequestException"/> that has a status code is a response.
    /// </summary>
    private static bool IsConnectionFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Net.Sockets.SocketException or IOException)
                return true;
            if (current is HttpRequestException { StatusCode: null })
                return true;
        }

        return false;
    }

    private sealed class SharedResolve
    {
        public TaskCompletionSource<M3U8Response?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenSource Cancellation { get; } = new();

        public int Waiters { get; set; } = 1;
    }

    private Task<M3U8Response?> ResolveViaRemoteAsync(
        string streamUrl,
        string? playerStreamName,
        string? resolutionStrategy,
        string? source,
        CancellationToken cancellationToken)
    {
        if (remoteCompute?.IsPaired != true)
        {
            return Task.FromResult<M3U8Response?>(null);
        }

        _usedRemote = true;
        logger.LogInformation("[LocalLanPlay] No LAN service; resolving {Url} via paired compute host", streamUrl);
        var useMp = ShouldUseMpEndpoint(resolutionStrategy, source, ["play.stream", "mp", "mp.chrome"]);
        return remoteCompute.ResolveAsync(useMp, streamUrl, playerStreamName, cancellationToken);
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
        catch (Exception ex) when (IsCallerCancellation(ex, cancellationToken))
        {
            throw ToCallerCancellation(ex, cancellationToken);
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
            RecordServiceResponded();
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
        catch (Exception ex) when (IsCallerCancellation(ex, cancellationToken))
        {
            throw ToCallerCancellation(ex, cancellationToken);
        }
        catch (Exception ex)
        {
            LastHealthStatus = null;
            logger.LogInformation(ex, "[LocalLanPlay] Health check request failed for {Url}. " +
                "Unable to connect to discovered local service endpoint.",
                healthUrl);
            // Only "no HTTP response" counts: a refused connection, or our 3s
            // timeout with nothing back. A bad health body is still a response.
            if (ex is OperationCanceledException || IsConnectionFailure(ex))
                RecordConnectionFailure(baseUrl, ex);
            return false;
        }
    }

    /// <summary>Our own cap on one /mp harvest (settable for tests).</summary>
    internal TimeSpan MpHarvestTimeout { get; set; } = TimeSpan.FromSeconds(330);

    private Task<M3U8Response?> CallMpEndpointAsync(
        string baseUrl,
        string streamUrl,
        string? playerStreamName,
        CancellationToken cancellationToken)
    {
        if (IsMpDisabledRemembered(baseUrl))
        {
            return Task.FromException<M3U8Response?>(MpDisabledException());
        }

        lock (_mpHarvestGate)
        {
            if (_mpHarvest?.CanReuseFor(_mpHarvestPage, streamUrl) == true)
            {
                return _mpHarvest.WaitForAsync(playerStreamName, cancellationToken);
            }
        }

        return StartMpHarvestAsync(baseUrl, streamUrl, playerStreamName, cancellationToken);
    }

    private async Task<M3U8Response?> StartMpHarvestAsync(
        string baseUrl,
        string streamUrl,
        string? playerStreamName,
        CancellationToken cancellationToken)
    {
        var harvest = new MpHarvestSession();
        lock (_mpHarvestGate)
        {
            _mpHarvest = harvest;
            _mpHarvestPage = streamUrl;
        }

        _ = ReadMpHarvestAsync(baseUrl, streamUrl, playerStreamName, harvest, cancellationToken);
        return await harvest.WaitForAsync(playerStreamName, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadMpHarvestAsync(
        string baseUrl,
        string streamUrl,
        string? playerStreamName,
        MpHarvestSession harvest,
        CancellationToken cancellationToken)
    {
        var url = $"{baseUrl.TrimEnd('/')}/mp";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(MpHarvestTimeout);

            var payload = JsonSerializer.Serialize(new
            {
                pageUrl = streamUrl,
                stream = string.IsNullOrWhiteSpace(playerStreamName) ? null : playerStreamName.Trim(),
                streamResults = true
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            ApplyDohHeaders(request);
            request.Headers.TryAddWithoutValidation(MpNdjson.RequestHeaderName, "1");
            request.Headers.Accept.ParseAdd(MpNdjson.ContentType);
            logger.LogInformation(
                "[LocalLanPlay] Resolving MP stream via POST {Url}{StreamSuffix} (stream)",
                url,
                string.IsNullOrWhiteSpace(playerStreamName) ? "" : $" (stream={playerStreamName.Trim()})");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            RecordServiceResponded();
            // A 401/403 or a 5xx is the service talking about itself, not about
            // the stream: fail the harvest as infrastructure so the resolver
            // defers the row (never Failed, never reported). Only a 2xx with no
            // playlist, or a 4xx such as 404 "M3U8 not captured", is a verdict.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                logger.LogWarning(
                    "[LocalLanPlay] POST /mp rejected with 401 Unauthorized for {StreamUrl}. Local service requires a valid Auth0 access token.",
                    streamUrl);
                harvest.Fail(new LocalServiceUnavailableException(
                    LocalServiceUnavailableReason.Unauthorized, "POST /mp rejected with 401 Unauthorized"));
                return;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                logger.LogWarning(
                    "[LocalLanPlay] POST /mp rejected with 403 Forbidden for {StreamUrl}. User lacks required stream-viewer role.",
                    streamUrl);
                harvest.Fail(new LocalServiceUnavailableException(
                    LocalServiceUnavailableReason.Unauthorized, "POST /mp rejected with 403 Forbidden"));
                return;
            }

            if ((int)response.StatusCode >= 500)
            {
                var body = await ReadErrorBodyAsync(response, cts.Token).ConfigureAwait(false);
                if (IsMpScrapeDisabled(body))
                {
                    RememberMpDisabled(baseUrl);
                    logger.LogInformation(
                        "[LocalLanPlay] POST /mp rejected by {BaseUrl}: V2/MP scrape disabled. {StreamUrl} is unavailable here, not dead (deferred, not reported)",
                        baseUrl, streamUrl);
                    harvest.Fail(MpDisabledException());
                }
                else
                {
                    logger.LogWarning(
                        "[LocalLanPlay] POST /mp returned {StatusCode} for {StreamUrl}: {Body}. Local service error, not a stream verdict",
                        (int)response.StatusCode, streamUrl, body);
                    harvest.Fail(new LocalServiceUnavailableException(
                        LocalServiceUnavailableReason.ServiceError,
                        $"POST /mp returned {(int)response.StatusCode}"));
                }

                return;
            }

            if (response.Content is null)
            {
                harvest.Complete(failed: true);
                return;
            }

            if (!MpNdjson.IsNdjson(response.Content.Headers.ContentType?.MediaType))
            {
                var json = await response.Content.ReadAsStringAsync(cts.Token);
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

                if (result is not null)
                {
                    harvest.ApplyLegacy(result);
                }
                else
                {
                    harvest.Complete(failed: true);
                }

                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            await foreach (var line in MpNdjson.ReadLinesAsync(stream, cts.Token))
            {
                var ev = MpNdjson.ParseLine(line);
                if (ev is not null)
                {
                    harvest.Apply(ev);
                }
            }

            if (!harvest.IsCompleted)
            {
                harvest.Complete(failed: false);
            }

            logger.LogInformation(
                "[LocalLanPlay] Finished MP harvest for {StreamUrl} chips={Chips}",
                streamUrl,
                harvest.Streams.Count == 0 ? "(none)" : string.Join(", ", harvest.Streams));
        }
        catch (Exception ex) when (IsCallerCancellation(ex, cancellationToken))
        {
            // Android aborts a cancelled socket as SocketException 125 wrapped
            // in HttpRequestException, not OperationCanceledException. Completing
            // the harvest as a failure makes the caller invalidate discovery.
            logger.LogDebug(ex, "[LocalLanPlay] POST /mp cancelled for {StreamUrl}", streamUrl);
            harvest.Cancel();
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            // Our MpHarvestTimeout, not the caller. Chrome may still be soaking:
            // that proves nothing about the stream.
            var alive = false;
            try
            {
                alive = await CheckHealthAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception healthEx) when (IsCallerCancellation(healthEx, cancellationToken))
            {
                harvest.Cancel();
                return;
            }
            catch (Exception healthEx)
            {
                logger.LogDebug(healthEx, "[LocalLanPlay] Health check after POST /mp timeout failed");
            }

            logger.LogInformation(
                "[LocalLanPlay] POST /mp for {StreamUrl} exceeded our {Timeout}s timeout (service alive: {Alive}); deferring, not failing",
                streamUrl, MpHarvestTimeout.TotalSeconds, alive);
            harvest.Fail(new LocalServiceUnavailableException(
                alive || LastHealthStatus is not null
                    ? LocalServiceUnavailableReason.CallTimeout
                    : LocalServiceUnavailableReason.ConnectionFailed,
                $"POST /mp exceeded {MpHarvestTimeout.TotalSeconds:F0}s", ex));
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            logger.LogInformation(ex, "[LocalLanPlay] Could not connect to local service {BaseUrl} for POST /mp {StreamUrl}",
                baseUrl, streamUrl);
            RecordConnectionFailure(baseUrl, ex);
            harvest.Fail(new LocalServiceUnavailableException(
                LocalServiceUnavailableReason.ConnectionFailed,
                "Connection to local service failed during POST /mp", ex));
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "[LocalLanPlay] Failed POST /mp via {BaseUrl} for {StreamUrl}",
                baseUrl, streamUrl);
            harvest.Fail(new LocalServiceUnavailableException(
                LocalServiceUnavailableReason.ConnectionFailed,
                "POST /mp failed", ex));
        }
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
            return string.Empty;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Length > 500 ? body[..500] : body;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// LocalService answers <c>503 {"error":"V2/MP scrape disabled on this local service"}</c>
    /// when its MP gate is off (log line: <c>/mp rejected: V2/MP scrape disabled</c>).
    /// </summary>
    internal static bool IsMpScrapeDisabled(string? body) =>
        !string.IsNullOrEmpty(body)
        && body.Contains("scrape disabled", StringComparison.OrdinalIgnoreCase);

    private static LocalServiceUnavailableException MpDisabledException() =>
        new(LocalServiceUnavailableReason.MpDisabled, "V2/MP scrape disabled on the local service");

    private void RememberMpDisabled(string baseUrl)
    {
        lock (_mpDisabledGate)
        {
            _mpDisabledBaseUrl = baseUrl;
            _mpDisabledAt = Clock();
        }
    }

    private bool IsMpDisabledRemembered(string baseUrl)
    {
        lock (_mpDisabledGate)
        {
            if (_mpDisabledBaseUrl is null)
                return false;
            if (!string.Equals(_mpDisabledBaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase)
                || Clock() - _mpDisabledAt >= MpDisabledMemory)
            {
                _mpDisabledBaseUrl = null;
                return false;
            }

            return true;
        }
    }

    private async Task<M3U8Response?> CallPlayEndpointAsync(
        string baseUrl,
        string streamUrl,
        string? playerStreamName,
        string resolveKey,
        CancellationToken cancellationToken)
    {
        var url = $"{baseUrl.TrimEnd('/')}/play/{Uri.EscapeDataString(streamUrl)}";
        var callTimeout = PlayTimeoutFor(resolveKey);
        if (!string.IsNullOrWhiteSpace(playerStreamName))
        {
            url += $"?stream={Uri.EscapeDataString(playerStreamName.Trim())}";
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(callTimeout);

            logger.LogDebug("[LocalLanPlay] Resolving stream via local service: {Url} (timeout {Timeout}s)",
                url, callTimeout.TotalSeconds);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyDohHeaders(request);
            var response = await httpClient.SendAsync(request, cts.Token);
            RecordServiceResponded();
            _slowStreams.TryRemove(resolveKey, out _);
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
                    // Our token, not the stream: never a Failed row or a crowd report.
                    throw new LocalServiceUnavailableException(
                        LocalServiceUnavailableReason.Unauthorized, "GET /play rejected with 401 Unauthorized");
                }
                else if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    logger.LogWarning(
                        "[LocalLanPlay] GET /play rejected with 403 Forbidden for {StreamUrl}. User lacks required stream-viewer role.",
                        streamUrl);
                    throw new LocalServiceUnavailableException(
                        LocalServiceUnavailableReason.Unauthorized, "GET /play rejected with 403 Forbidden");
                }
                else if (LocalServicePlayFailure.TryDescribeCdnUnreachable((int)response.StatusCode, json, out var cdnReason))
                {
                    // The service ran, but could not reach the stream's CDN (IP block,
                    // CDN down). Not a dead stream: defer it for the CDN recheck.
                    logger.LogWarning(
                        "[LocalLanPlay] GET /play returned {StatusCode} for {StreamUrl}: CDN unreachable ({Reason}). Deferred, not dead",
                        (int)response.StatusCode, streamUrl, cdnReason);
                    throw new LocalServiceUnavailableException(
                        LocalServiceUnavailableReason.CdnUnreachable,
                        $"{CdnReachabilityPolicy.RecheckingMessage} ({cdnReason})");
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
        catch (Exception ex) when (IsCallerCancellation(ex, cancellationToken))
        {
            // A caller-cancelled /play is not a dead local service. Android
            // surfaces that abort as SocketException 125 / HttpRequestException
            // / TaskCanceledException, which must not fall through to "return
            // null" and invalidate discovery.
            throw ToCallerCancellation(ex, cancellationToken);
        }
        catch (LocalServiceUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            // Our own timeout, not the caller's. The service is often still
            // resolving (it found techydeals' m3u8 after our 20s cutoff, twice).
            // That proves nothing about the stream: remember it as slow so the
            // next attempt waits longer, and check that the service is alive.
            _slowStreams[resolveKey] = DateTimeOffset.UtcNow;
            var alive = await CheckHealthAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "[LocalLanPlay] GET /play for {StreamUrl} exceeded our {Timeout}s timeout (service alive: {Alive}); will retry with a longer timeout",
                streamUrl, callTimeout.TotalSeconds, alive);
            throw new LocalServiceUnavailableException(
                alive || LastHealthStatus is not null
                    ? LocalServiceUnavailableReason.CallTimeout
                    : LocalServiceUnavailableReason.ConnectionFailed,
                $"GET /play exceeded {callTimeout.TotalSeconds:F0}s", ex);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            logger.LogInformation(ex, "[LocalLanPlay] Could not connect to local service endpoint {BaseUrl} for {StreamUrl}",
                baseUrl, streamUrl);
            RecordConnectionFailure(baseUrl, ex);
            throw new LocalServiceUnavailableException(
                LocalServiceUnavailableReason.ConnectionFailed,
                "Connection to local service failed", ex);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "[LocalLanPlay] Failed to resolve m3u8 via local service endpoint {BaseUrl}. " +
                "Stream URL may be inaccessible or service may be temporarily unavailable.",
                baseUrl);
            throw new LocalServiceUnavailableException(
                LocalServiceUnavailableReason.ConnectionFailed,
                "Local service call failed", ex);
        }
    }

    /// <summary>
    /// True when <paramref name="cancellationToken"/> is cancelled, including
    /// when the HTTP stack reported that abort as a socket error or a
    /// cancelled task instead of <see cref="OperationCanceledException"/>.
    /// A linked-CTS timeout leaves the caller token alone and stays a failure.
    /// </summary>
    private static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return true;

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException canceled
                && canceled.CancellationToken.CanBeCanceled
                && canceled.CancellationToken == cancellationToken)
            {
                return true;
            }
        }

        return false;
    }

    private static OperationCanceledException ToCallerCancellation(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException canceled && canceled.CancellationToken == cancellationToken)
            throw canceled;

        return new OperationCanceledException("Local service call was cancelled.", exception, cancellationToken);
    }

    public Task<string?> GetServiceBaseUrlAsync(CancellationToken cancellationToken = default) =>
        ResolveServiceBaseUrlAsync(cancellationToken);

    private void ApplyDohHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(connection?.Id))
            request.Headers.TryAddWithoutValidation(LocalServiceConnection.HeaderName, connection.Id);

        if (dnsPreferences is null)
            return;

        var enabled = dnsPreferences.LoadDnsOverHttpsFallbackEnabled();
        request.Headers.TryAddWithoutValidation("X-Vardy-Doh", enabled ? "1" : "0");
        if (!enabled)
            return;

        var resolver = dnsEndpoint?.Address?.ToString();
        if (!string.IsNullOrWhiteSpace(resolver))
            request.Headers.TryAddWithoutValidation("X-Vardy-Doh-Resolver", resolver);
    }

    /// <summary>
    /// The local service endpoint. The last good endpoint is used even after
    /// its TTL expires (a background probe refreshes it). When none has worked
    /// yet, discovery is tried <c>1 + DiscoveryRetryDelays.Length</c> times with
    /// backoff before giving up, so one missed UDP reply doesn't fail queued work.
    /// </summary>
    private async Task<string?> ResolveServiceBaseUrlAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var url = await LookupServiceBaseUrlOnceAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(url) || attempt >= DiscoveryRetryDelays.Length)
                return url;

            logger.LogInformation("[LocalLanPlay] Discovery attempt {Attempt} found no local service; retrying in {Delay}s",
                attempt + 1, DiscoveryRetryDelays[attempt].TotalSeconds);
            await DelayAsync(DiscoveryRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> LookupServiceBaseUrlOnceAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var lookup = _endpoints.LookupOrJoin(now, cancellationToken, ct =>
            DiscoverOverrideForTests is { } discover
                ? discover(ct)
                : DiscoverViaUdpAsync(ct));
        switch (lookup.Kind)
        {
            case EndpointLookupKind.Cached:
                logger.LogDebug("[LocalLanPlay] Using cached service endpoint: {Endpoint} (expires in {TTL}s)",
                    lookup.Url, lookup.Remaining.TotalSeconds);
                return lookup.Url;
            case EndpointLookupKind.Stale:
                logger.LogDebug("[LocalLanPlay] Using last good service endpoint {Endpoint} while discovery refreshes",
                    lookup.Url);
                return lookup.Url;
            case EndpointLookupKind.Backoff:
                logger.LogDebug("[LocalLanPlay] Skipping UDP discovery: previous attempt failed recently (backoff {Remaining:F1}s)",
                    lookup.Remaining.TotalSeconds);
                return null;
            default:
                logger.LogDebug("[LocalLanPlay] Cache miss or expired, performing fresh discovery...");
                var discovered = await lookup.Flight!.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(discovered))
                {
                    logger.LogInformation("[LocalLanPlay] Cached discovered service endpoint: {Endpoint} (TTL: {TTL}s)",
                        discovered, DiscoveryCacheTtl.TotalSeconds);
                }

                return discovered;
        }
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
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            throw ToCallerCancellation(ex, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LocalLanPlay] UDP discovery failed with exception. Verify VardyParty.LocalService is installed and running on your network.");
            return null;
        }
    }

    private void InvalidateDiscoveryCache()
    {
        _endpoints.Invalidate();
        _cachedCapabilities = [];
        _cachedServiceVersion = null;
        _capabilitiesCachedAt = DateTimeOffset.MinValue;
    }
}