using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

/// <summary>
/// Unit rules for <see cref="CdnReachabilityPolicy"/>: "CDN unreachable" is
/// never "dead". During the MSI IP block (19:02 to 19:44 UK time on 31 Oct
/// 2026) connect failures marked 27 lshls rows plus durrfity, 643t8a, nothintd
/// and instreams dead; all were Healthy again after 19:52.
/// </summary>
public class CdnReachabilityPolicyTests
{
    public static TheoryData<Exception, StreamHealthStatus?> TransportFailures => new()
    {
        { new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (hls.lshls.st:443)", new SocketException(10061)), StreamHealthStatus.CdnUnreachable },
        { new HttpRequestException("An error occurred", new SocketException((int)SocketError.ConnectionRefused)), StreamHealthStatus.CdnUnreachable },
        { new HttpRequestException("An error occurred", new SocketException((int)SocketError.TimedOut)), StreamHealthStatus.CdnUnreachable },
        { new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established", new AuthenticationException("handshake failed")), StreamHealthStatus.CdnUnreachable },
        { new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"), StreamHealthStatus.CdnUnreachable },
        { new IOException("Unable to read data from the transport connection: Connection reset by peer"), StreamHealthStatus.CdnUnreachable },
        { new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known (cdn.gone.example)", new SocketException(11001)), StreamHealthStatus.HostNotResolved },
        { new HttpRequestException("Name or service not known (cdn.gone.example:443)"), StreamHealthStatus.HostNotResolved },
        { new InvalidOperationException("An invalid request URI was provided."), null },
        { new FormatException("bad body"), null }
    };

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public void ClassifyTransportFailure_ConnectIsCdnUnreachable_DnsIsDead_OtherIsNull(Exception ex, StreamHealthStatus? expected)
        => Assert.Equal(expected, CdnReachabilityPolicy.ClassifyTransportFailure(ex));

    [Fact]
    public void ClassifyTransportFailure_CallerCancelled_IsNoVerdict()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(CdnReachabilityPolicy.ClassifyTransportFailure(new OperationCanceledException(cts.Token), cts.Token));
    }

    [Theory]
    [InlineData(200, StreamHealthStatus.Healthy)]
    [InlineData(404, StreamHealthStatus.ManifestNotFound)]
    [InlineData(410, StreamHealthStatus.ManifestNotFound)]
    [InlineData(403, StreamHealthStatus.ManifestUnreachable)]
    [InlineData(500, StreamHealthStatus.ManifestUnreachable)]
    [InlineData(503, StreamHealthStatus.ManifestUnreachable)]
    public void ClassifyManifestHttpStatus_OnlyNotFoundIsDead(int code, StreamHealthStatus expected)
        => Assert.Equal(expected, CdnReachabilityPolicy.ClassifyManifestHttpStatus(code));

    [Theory]
    [InlineData(StreamHealthStatus.CdnUnreachable, true)]
    [InlineData(StreamHealthStatus.ManifestUnreachable, true)]
    [InlineData(StreamHealthStatus.ManifestNotFound, false)]
    [InlineData(StreamHealthStatus.HostNotResolved, false)]
    [InlineData(StreamHealthStatus.SegmentUnreachable, false)]
    [InlineData(StreamHealthStatus.EmptyManifest, false)]
    [InlineData(StreamHealthStatus.Healthy, false)]
    public void IsCdnUnreachable_OnlyConnectClassStatuses(StreamHealthStatus status, bool expected)
        => Assert.Equal(expected, CdnReachabilityPolicy.IsCdnUnreachable(status));

    [Theory]
    [InlineData("page.goto: net::ERR_CONNECTION_TIMED_OUT at https://hls.lshls.st/x", true)]
    [InlineData("net::ERR_CONNECTION_REFUSED", true)]
    [InlineData("net::ERR_CONNECTION_RESET", true)]
    [InlineData("net::ERR_SSL_PROTOCOL_ERROR", true)]
    [InlineData("net::ERR_TIMED_OUT", true)]
    [InlineData("net::ERR_NAME_NOT_RESOLVED", false)]
    [InlineData("M3U8 not Found", false)]
    [InlineData(null, false)]
    public void IsChromiumCdnUnreachable_ConnectErrorsOnly_NeverNameNotResolved(string? text, bool expected)
        => Assert.Equal(expected, CdnReachabilityPolicy.IsChromiumCdnUnreachable(text));

    [Theory]
    [InlineData("ConnectException: Failed to connect to /1.2.3.4:443", false, true)]
    [InlineData("SocketTimeoutException: connect timed out", false, true)]
    [InlineData("SSLHandshakeException: Connection closed by peer", false, true)]
    [InlineData("UnknownHostException: Unable to resolve host \"cdn.x\"", true, false)]
    [InlineData("Response code: 404", false, false)]
    public void Mentions_DnsAndConnectAreSeparate(string text, bool dns, bool connect)
    {
        Assert.Equal(dns, CdnReachabilityPolicy.MentionsHostNotResolved(text));
        Assert.Equal(connect, CdnReachabilityPolicy.MentionsConnectFailure(text));
    }

    [Fact]
    public void RecheckDelay_TwoThenFiveThenEveryTen()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), CdnReachabilityPolicy.RecheckDelay(0));
        Assert.Equal(TimeSpan.FromMinutes(5), CdnReachabilityPolicy.RecheckDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(10), CdnReachabilityPolicy.RecheckDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(10), CdnReachabilityPolicy.RecheckDelay(9));
        Assert.Equal(TimeSpan.FromMinutes(2), CdnReachabilityPolicy.RecheckDelay(-1));
    }

    [Fact]
    public void ShouldKeepRechecking_WhileLiveOnly()
    {
        var kickOff = new DateTime(2026, 10, 31, 19, 0, 0, DateTimeKind.Utc);
        var live = new Game { Start = kickOff, IsFinished = false };
        var finished = new Game { Start = kickOff, IsFinished = true };
        var noStart = new Game { IsFinished = false };

        Assert.True(CdnReachabilityPolicy.ShouldKeepRechecking(live, kickOff.AddMinutes(52)));
        Assert.True(CdnReachabilityPolicy.ShouldKeepRechecking(live, kickOff + CdnReachabilityPolicy.MaxLiveWindow));
        Assert.False(CdnReachabilityPolicy.ShouldKeepRechecking(live, kickOff + CdnReachabilityPolicy.MaxLiveWindow + TimeSpan.FromMinutes(1)));
        Assert.False(CdnReachabilityPolicy.ShouldKeepRechecking(finished, kickOff.AddMinutes(30)));
        Assert.True(CdnReachabilityPolicy.ShouldKeepRechecking(noStart, kickOff));
        Assert.True(CdnReachabilityPolicy.ShouldKeepRechecking(null, kickOff));
    }
}
