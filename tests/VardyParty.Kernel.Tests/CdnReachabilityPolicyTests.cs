using System;
using System.IO;
using System.Linq;
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
/// never "dead". During an ISP IP block (19:02 to 19:44 UK time on 31 Oct
/// 2026) connect failures marked many live rows dead; all were Healthy again
/// after 19:52.
/// </summary>
public class CdnReachabilityPolicyTests
{
    public static TheoryData<Exception, StreamHealthStatus?> TransportFailures => new()
    {
        { new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (hls.cdn-alpha.example:443)", new SocketException(10061)), StreamHealthStatus.CdnUnreachable },
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
    {
        // Arrange — inputs from MemberData

        // Act
        var status = CdnReachabilityPolicy.ClassifyTransportFailure(ex);

        // Assert
        Assert.Equal(expected, status);
    }

    [Fact]
    public void ClassifyTransportFailure_CallerCancelled_IsNoVerdict()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var status = CdnReachabilityPolicy.ClassifyTransportFailure(new OperationCanceledException(cts.Token), cts.Token);

        // Assert
        Assert.Null(status);
    }

    [Theory]
    [InlineData(200, StreamHealthStatus.Healthy)]
    [InlineData(404, StreamHealthStatus.ManifestNotFound)]
    [InlineData(410, StreamHealthStatus.ManifestNotFound)]
    [InlineData(403, StreamHealthStatus.ManifestUnreachable)]
    [InlineData(500, StreamHealthStatus.ManifestUnreachable)]
    [InlineData(503, StreamHealthStatus.ManifestUnreachable)]
    public void ClassifyManifestHttpStatus_OnlyNotFoundIsDead(int code, StreamHealthStatus expected)
    {
        // Arrange — code from InlineData

        // Act
        var status = CdnReachabilityPolicy.ClassifyManifestHttpStatus(code);

        // Assert
        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData(StreamHealthStatus.CdnUnreachable, true)]
    [InlineData(StreamHealthStatus.ManifestUnreachable, true)]
    [InlineData(StreamHealthStatus.ManifestNotFound, false)]
    [InlineData(StreamHealthStatus.HostNotResolved, false)]
    [InlineData(StreamHealthStatus.SegmentUnreachable, false)]
    [InlineData(StreamHealthStatus.EmptyManifest, false)]
    [InlineData(StreamHealthStatus.Healthy, false)]
    public void IsCdnUnreachable_OnlyConnectClassStatuses(StreamHealthStatus status, bool expected)
    {
        // Arrange — status from InlineData

        // Act
        var isUnreachable = CdnReachabilityPolicy.IsCdnUnreachable(status);

        // Assert
        Assert.Equal(expected, isUnreachable);
    }

    [Theory]
    [InlineData("page.goto: net::ERR_CONNECTION_TIMED_OUT at https://hls.cdn-alpha.example/x", true)]
    [InlineData("net::ERR_CONNECTION_REFUSED", true)]
    [InlineData("net::ERR_CONNECTION_RESET", true)]
    [InlineData("net::ERR_SSL_PROTOCOL_ERROR", true)]
    [InlineData("net::ERR_TIMED_OUT", true)]
    [InlineData("net::ERR_NAME_NOT_RESOLVED", false)]
    [InlineData("M3U8 not Found", false)]
    [InlineData(null, false)]
    public void IsChromiumCdnUnreachable_ConnectErrorsOnly_NeverNameNotResolved(string? text, bool expected)
    {
        // Arrange — text from InlineData

        // Act
        var isUnreachable = CdnReachabilityPolicy.IsChromiumCdnUnreachable(text);

        // Assert
        Assert.Equal(expected, isUnreachable);
    }

    [Theory]
    [InlineData("ConnectException: Failed to connect to /1.2.3.4:443", false, true)]
    [InlineData("SocketTimeoutException: connect timed out", false, true)]
    [InlineData("SSLHandshakeException: Connection closed by peer", false, true)]
    [InlineData("UnknownHostException: Unable to resolve host \"cdn.x\"", true, false)]
    [InlineData("Response code: 404", false, false)]
    public void Mentions_DnsAndConnectAreSeparate(string text, bool dns, bool connect)
    {
        // Arrange — text from InlineData

        // Act
        var mentionsDns = CdnReachabilityPolicy.MentionsHostNotResolved(text);
        var mentionsConnect = CdnReachabilityPolicy.MentionsConnectFailure(text);

        // Assert
        Assert.Equal(dns, mentionsDns);
        Assert.Equal(connect, mentionsConnect);
    }

    [Fact]
    public void RecheckDelay_TwoThenFiveThenEveryTen()
    {
        // Arrange
        var attempts = new[] { 0, 1, 2, 9, -1 };

        // Act
        var delays = attempts.Select(CdnReachabilityPolicy.RecheckDelay).ToArray();

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(2), delays[0]);
        Assert.Equal(TimeSpan.FromMinutes(5), delays[1]);
        Assert.Equal(TimeSpan.FromMinutes(10), delays[2]);
        Assert.Equal(TimeSpan.FromMinutes(10), delays[3]);
        Assert.Equal(TimeSpan.FromMinutes(2), delays[4]);
    }

    [Fact]
    public void RecheckDelay_NoBackup_OnlyTheFirstRecheckIs30s()
    {
        // Arrange - Jon, 13 Nov 2026: a shorter first recheck while there is no backup.
        var attempts = new[] { 0, 1, 2, 9 };

        // Act
        var noBackup = attempts.Select(a => CdnReachabilityPolicy.RecheckDelay(a, noBackup: true)).ToArray();
        var withBackup = attempts.Select(a => CdnReachabilityPolicy.RecheckDelay(a, noBackup: false)).ToArray();

        // Assert
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)], noBackup);
        Assert.Equal(attempts.Select(CdnReachabilityPolicy.RecheckDelay), withBackup);
    }

    [Fact]
    public void ShouldKeepRechecking_WhileLiveOnly()
    {
        // Arrange
        var kickOff = new DateTime(2026, 10, 31, 19, 0, 0, DateTimeKind.Utc);
        var live = new Game { Start = kickOff, IsFinished = false };
        var finished = new Game { Start = kickOff, IsFinished = true };
        var noStart = new Game { IsFinished = false };

        // Act
        var midMatch = CdnReachabilityPolicy.ShouldKeepRechecking(live, kickOff.AddMinutes(52));
        var atWindow = CdnReachabilityPolicy.ShouldKeepRechecking(live, kickOff + CdnReachabilityPolicy.MaxLiveWindow);
        var pastWindow = CdnReachabilityPolicy.ShouldKeepRechecking(live, kickOff + CdnReachabilityPolicy.MaxLiveWindow + TimeSpan.FromMinutes(1));
        var finishedGame = CdnReachabilityPolicy.ShouldKeepRechecking(finished, kickOff.AddMinutes(30));
        var withoutStart = CdnReachabilityPolicy.ShouldKeepRechecking(noStart, kickOff);
        var nullGame = CdnReachabilityPolicy.ShouldKeepRechecking(null, kickOff);

        // Assert
        Assert.True(midMatch);
        Assert.True(atWindow);
        Assert.False(pastWindow);
        Assert.False(finishedGame);
        Assert.True(withoutStart);
        Assert.True(nullGame);
    }
}
