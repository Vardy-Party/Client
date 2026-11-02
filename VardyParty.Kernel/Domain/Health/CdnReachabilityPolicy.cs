using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace VardyParty.Kernel;

/// <summary>
/// Why a stream is deferred instead of resolved. Neither reason says anything
/// about the stream itself, so neither is ever reported to the health API.
/// </summary>
public enum StreamDeferReason
{
    /// <summary>Not deferred.</summary>
    None,

    /// <summary>The local service did not answer (unreachable, timed out, failed itself).</summary>
    LocalService,

    /// <summary>
    /// The CDN host could not be reached from this network: connect timeout,
    /// connection refused or reset, or a TLS failure to the host. During the
    /// MSI IP block (19:02 to 19:44 UK time on 31 Oct 2026) 27 lshls rows plus
    /// durrfity, 643t8a, nothintd and instreams were marked dead for this and
    /// all came back Healthy after 19:52. See <see cref="CdnReachabilityPolicy"/>.
    /// </summary>
    CdnUnreachable
}

/// <summary>
/// "CDN unreachable" is never "dead". A host we cannot connect to (timeout,
/// refused, reset, TLS) is a network problem on our side or a temporary block,
/// so the row is deferred and rechecked after a back-off, never reported as
/// failed and never dropped.
/// <para>
/// Still dead (reported, dropped): HTTP 404/410 on the manifest
/// (<see cref="StreamHealthStatus.ManifestNotFound"/>), "M3U8 not Found" from
/// the local service, decoy or non-video segments
/// (<see cref="StreamHealthStatus.SegmentUnreachable"/>), and DNS
/// NAME_NOT_RESOLVED (<see cref="StreamHealthStatus.HostNotResolved"/>). DNS stays
/// dead by choice: a name that does not resolve is almost always a retired
/// or fake host, and keeping it would recheck it all game.
/// </para>
/// </summary>
public static class CdnReachabilityPolicy
{
    /// <summary>The status and error text shown for a CDN-deferred row.</summary>
    public const string RecheckingMessage = "CDN unreachable, rechecking";

    /// <summary>Back-off between rechecks: 2 min, then 5 min, then every 10 min.</summary>
    public static readonly IReadOnlyList<TimeSpan> RecheckDelays =
    [
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10)
    ];

    /// <summary>
    /// How long after kick-off a game still counts as live for rechecks when
    /// the snapshot does not say it has finished (extra time, penalties, a
    /// late kick-off).
    /// </summary>
    public static readonly TimeSpan MaxLiveWindow = TimeSpan.FromHours(4);

    /// <summary>The delay before recheck number <paramref name="attempt"/> (0-based).</summary>
    public static TimeSpan RecheckDelay(int attempt) =>
        RecheckDelays[Math.Clamp(attempt, 0, RecheckDelays.Count - 1)];

    /// <summary>
    /// Keep rechecking while the game is live: not finished and within
    /// <see cref="MaxLiveWindow"/> of kick-off. A game with no start time keeps
    /// rechecking until the session ends.
    /// </summary>
    public static bool ShouldKeepRechecking(Game? game, DateTime utcNow)
    {
        if (game is null)
            return true;
        if (game.IsFinished)
            return false;
        if (game.Start == default)
            return true;

        var startUtc = game.Start.Kind == DateTimeKind.Utc ? game.Start : game.Start.ToUniversalTime();
        return utcNow <= startUtc + MaxLiveWindow;
    }

    /// <summary>
    /// Health statuses that mean "could not reach the CDN", not "the stream is
    /// dead". <see cref="StreamHealthStatus.ManifestUnreachable"/> (manifest 403,
    /// 5xx, or a transport failure we could not classify) is included: it never
    /// proved the playlist is gone.
    /// </summary>
    public static bool IsCdnUnreachable(StreamHealthStatus status) =>
        status is StreamHealthStatus.CdnUnreachable or StreamHealthStatus.ManifestUnreachable;

    /// <summary>
    /// Manifest HTTP answer: 404/410 is dead (<see cref="StreamHealthStatus.ManifestNotFound"/>);
    /// any other non-2xx is <see cref="StreamHealthStatus.ManifestUnreachable"/> (deferred).
    /// </summary>
    public static StreamHealthStatus ClassifyManifestHttpStatus(int statusCode) => statusCode switch
    {
        >= 200 and < 300 => StreamHealthStatus.Healthy,
        404 or 410 => StreamHealthStatus.ManifestNotFound,
        _ => StreamHealthStatus.ManifestUnreachable
    };

    private static readonly string[] DnsMarkers =
    [
        "unable to resolve host",
        "unknownhostexception",
        "no such host",
        "name or service not known",
        "name not resolved",
        "name_not_resolved",
        "nodename nor servname",
        "no address associated with hostname"
    ];

    private static readonly string[] ConnectMarkers =
    [
        "connection refused",
        "econnrefused",
        "actively refused",
        "connection timed out",
        "etimedout",
        "connect timed out",
        "sockettimeoutexception",
        "connection reset",
        "econnreset",
        "forcibly closed",
        "connection aborted",
        "no route to host",
        "network is unreachable",
        "host is unreachable",
        "failed to connect",
        "unable to connect",
        "connectexception",
        "ssl handshake",
        "sslhandshakeexception",
        "sslexception",
        "the ssl connection could not be established",
        "certificate",
        "handshake"
    ];

    /// <summary>True when the text names a DNS failure (kept dead).</summary>
    public static bool MentionsHostNotResolved(string? text) => ContainsAny(text, DnsMarkers);

    /// <summary>True when the text names a connect, reset, timeout or TLS failure (and not DNS).</summary>
    public static bool MentionsConnectFailure(string? text) =>
        !MentionsHostNotResolved(text) && ContainsAny(text, ConnectMarkers);

    /// <summary>
    /// Classifies an exception from an HTTP call to a CDN host.
    /// <list type="bullet">
    /// <item>DNS failure: <see cref="StreamHealthStatus.HostNotResolved"/> (dead).</item>
    /// <item>Connect failure, refused, reset, timeout, TLS, broken transport:
    /// <see cref="StreamHealthStatus.CdnUnreachable"/> (deferred).</item>
    /// <item>Anything else, or the caller cancelled: <c>null</c> (the caller keeps its own status).</item>
    /// </list>
    /// </summary>
    public static StreamHealthStatus? ClassifyTransportFailure(Exception? exception, CancellationToken callerToken = default)
    {
        if (exception is null || callerToken.IsCancellationRequested)
            return null;

        var chain = new List<Exception>();
        for (var e = exception; e != null && chain.Count < 8; e = e.InnerException)
            chain.Add(e);

        foreach (var e in chain)
        {
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
                return StreamHealthStatus.HostNotResolved;
            if (e is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData })
                return StreamHealthStatus.HostNotResolved;
            if (MentionsHostNotResolved(e.Message) || MentionsHostNotResolved(e.GetType().Name))
                return StreamHealthStatus.HostNotResolved;
        }

        foreach (var e in chain)
        {
            switch (e)
            {
                case HttpRequestException
                {
                    HttpRequestError: HttpRequestError.ConnectionError
                        or HttpRequestError.SecureConnectionError
                        or HttpRequestError.ProxyTunnelError
                        or HttpRequestError.ResponseEnded
                }:
                case SocketException:
                case AuthenticationException:
                case TimeoutException:
                case OperationCanceledException:
                case IOException:
                    return StreamHealthStatus.CdnUnreachable;
            }

            if (MentionsConnectFailure(e.Message) || MentionsConnectFailure(e.GetType().Name))
                return StreamHealthStatus.CdnUnreachable;
        }

        return null;
    }

    private static readonly string[] ChromiumConnectErrors =
    [
        "net::err_connection_timed_out",
        "net::err_connection_refused",
        "net::err_connection_reset",
        "net::err_connection_closed",
        "net::err_connection_aborted",
        "net::err_connection_failed",
        "net::err_empty_response",
        "net::err_timed_out",
        "net::err_address_unreachable",
        "net::err_network_access_denied",
        "net::err_tunnel_connection_failed",
        "net::err_proxy_connection_failed",
        "net::err_internet_disconnected",
        "net::err_network_changed",
        "net::err_ssl_",
        "net::err_cert_",
        "net::err_bad_ssl_client_auth_cert"
    ];

    /// <summary>
    /// A Chromium (LocalService browser) network error that means the host
    /// could not be reached: connection timed out, refused, reset, closed, TLS.
    /// Never <c>net::ERR_NAME_NOT_RESOLVED</c>, which stays dead.
    /// </summary>
    public static bool IsChromiumCdnUnreachable(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
            return false;
        if (errorText.Contains("net::err_name_not_resolved", StringComparison.OrdinalIgnoreCase))
            return ContainsAny(errorText.Replace("net::ERR_NAME_NOT_RESOLVED", string.Empty, StringComparison.OrdinalIgnoreCase), ChromiumConnectErrors);
        return ContainsAny(errorText, ChromiumConnectErrors);
    }

    private static bool ContainsAny(string? text, string[] markers)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
