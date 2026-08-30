using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using VardyParty.Hosting;
using Xunit;
using VardyParty.TestSupport;

namespace VardyParty.Hosting.Tests;

public class DualStackSocketsHttpHandlerTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void PlanConnect_KeepsBothFamiliesWithoutPreferringEither()
    {
        // Arrange
        var ipv6First = IPAddress.Parse("2001:db8:85a3::8a2e:370:7334");
        var ipv4 = new IPAddress([192, 0, 2, _fixture.Create<byte>()]);
        var ipv6Second = IPAddress.Parse("2001:db8::1");

        // Act
        var plan = DualStackSocketsHttpHandler.PlanConnect([ipv6First, ipv4, ipv6Second]);

        // Assert
        Assert.Equal([ipv6First, ipv6Second], plan.Ipv6);
        Assert.Equal([ipv4], plan.Ipv4);
    }

    [Fact]
    public void PlanConnect_PreservesRelativeOrderWithinAddressFamily()
    {
        // Arrange
        var ipv4North = new IPAddress([192, 0, 2, 10]);
        var ipv4South = new IPAddress([192, 0, 2, 20]);
        var ipv6East = IPAddress.Parse("2001:db8::10");
        var ipv6West = IPAddress.Parse("2001:db8::20");

        // Act
        var plan = DualStackSocketsHttpHandler.PlanConnect([ipv6East, ipv4North, ipv6West, ipv4South]);

        // Assert
        Assert.Equal([ipv6East, ipv6West], plan.Ipv6);
        Assert.Equal([ipv4North, ipv4South], plan.Ipv4);
    }

    [Fact]
    public void PlanConnect_WhenOnlyIPv6_LeavesIPv4Empty()
    {
        // Arrange
        var addresses = new List<IPAddress>
        {
            IPAddress.Parse("2001:db8::a"),
            IPAddress.Parse("2001:db8::b")
        };

        // Act
        var plan = DualStackSocketsHttpHandler.PlanConnect(addresses);

        // Assert
        Assert.Equal(addresses, plan.Ipv6);
        Assert.Empty(plan.Ipv4);
    }

    [Fact]
    public void WithSupplementalIpv4_FillsEmptyIpv4FromARecords()
    {
        // Arrange
        var ipv6 = IPAddress.Parse("2001:db8::1");
        var ipv4 = new IPAddress([192, 0, 2, 80]);
        var plan = DualStackSocketsHttpHandler.PlanConnect([ipv6]);

        // Act
        var merged = DualStackSocketsHttpHandler.WithSupplementalIpv4(plan, [ipv4]);

        // Assert
        Assert.Equal([ipv6], merged.Ipv6);
        Assert.Equal([ipv4], merged.Ipv4);
    }

    [Fact]
    public void WithSupplementalIpv4_DoesNotReplaceExistingIpv4()
    {
        // Arrange
        var ipv6 = IPAddress.Parse("2001:db8::1");
        var existing = new IPAddress([192, 0, 2, 10]);
        var extra = new IPAddress([192, 0, 2, 20]);
        var plan = DualStackSocketsHttpHandler.PlanConnect([ipv6, existing]);

        // Act
        var merged = DualStackSocketsHttpHandler.WithSupplementalIpv4(plan, [extra]);

        // Assert
        Assert.Equal([existing], merged.Ipv4);
    }

    [Fact]
    public void WithSupplementalIpv4_EmptyOrIpv6Extras_LeavesPlanUnchanged()
    {
        // Arrange
        var ipv6 = IPAddress.Parse("2001:db8::1");
        var plan = DualStackSocketsHttpHandler.PlanConnect([ipv6]);

        // Act
        var empty = DualStackSocketsHttpHandler.WithSupplementalIpv4(plan, []);
        var v6Only = DualStackSocketsHttpHandler.WithSupplementalIpv4(
            plan, [IPAddress.Parse("2001:db8::2")]);

        // Assert
        Assert.Empty(empty.Ipv4);
        Assert.Empty(v6Only.Ipv4);
        Assert.Equal(plan.Ipv6, empty.Ipv6);
    }

    [Fact]
    public void Create_ConfiguresConnectCallbackAndTimeouts()
    {
        // Arrange
        const bool ignoreSsl = false;

        // Act
        using var handler = DualStackSocketsHttpHandler.Create(ignoreSsl);

        // Assert
        Assert.NotNull(handler.ConnectCallback);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Equal(DualStackSocketsHttpHandler.ConnectTimeout, handler.ConnectTimeout);
        Assert.True(handler.UseCookies);
        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate, handler.AutomaticDecompression);
    }

    [Fact]
    public void Create_WhenIgnoreSsl_AcceptsNameMismatch()
    {
        // Arrange
        const bool ignoreSsl = true;

        // Act
        using var handler = DualStackSocketsHttpHandler.Create(ignoreSsl);

        // Assert
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.True(handler.SslOptions.RemoteCertificateValidationCallback(
            new object(),
            null,
            null,
            SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Create_WhenUseCookiesDisabled_DoesNotUseCookieContainer()
    {
        // Arrange
        const bool useCookies = false;

        // Act
        using var handler = DualStackSocketsHttpHandler.Create(useCookies: useCookies);

        // Assert
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task ConnectAsync_WhenIpv6Hangs_Ipv4Wins()
    {
        // Arrange
        var ipv6 = IPAddress.Parse("2001:db8::1");
        var ipv4 = new IPAddress([192, 0, 2, _fixture.Create<byte>()]);
        var port = 1024 + _fixture.Create<byte>();
        var plan = DualStackSocketsHttpHandler.PlanConnect([ipv6, ipv4]);
        var attempts = new ConcurrentQueue<IPAddress>();

        // Act
        await using var stream = await DualStackSocketsHttpHandler.ConnectAsync(
            plan,
            port,
            (address, _, cancellationToken) =>
            {
                attempts.Enqueue(address);
                return address.Equals(ipv6)
                    ? HangUntilCanceledAsync(cancellationToken)
                    : Task.FromResult<Stream>(new OakLaneStream(address));
            },
            TimeSpan.FromMilliseconds(20));

        // Assert
        var winner = Assert.IsType<OakLaneStream>(stream);
        Assert.Equal(ipv4, winner.Address);
        Assert.Contains(ipv6, attempts);
        Assert.Contains(ipv4, attempts);
    }

    [Fact]
    public async Task ConnectAsync_WhenIpv4Hangs_Ipv6Wins()
    {
        // Arrange
        var ipv6 = IPAddress.Parse("2001:db8::10");
        var ipv4 = new IPAddress([192, 0, 2, 20]);
        var port = 1024 + _fixture.Create<byte>();
        var plan = DualStackSocketsHttpHandler.PlanConnect([ipv6, ipv4]);
        var attempts = new ConcurrentQueue<IPAddress>();

        // Act
        await using var stream = await DualStackSocketsHttpHandler.ConnectAsync(
            plan,
            port,
            (address, _, cancellationToken) =>
            {
                attempts.Enqueue(address);
                if (address.Equals(ipv4))
                    return HangUntilCanceledAsync(cancellationToken);

                return SucceedAfterAsync(address, TimeSpan.FromMilliseconds(50), cancellationToken);
            },
            TimeSpan.FromMilliseconds(20));

        // Assert
        var winner = Assert.IsType<OakLaneStream>(stream);
        Assert.Equal(ipv6, winner.Address);
        Assert.Contains(ipv6, attempts);
        Assert.Contains(ipv4, attempts);
    }

    [Fact]
    public async Task ConnectAsync_WhenIpv6SucceedsImmediately_DoesNotWaitOnIpv4()
    {
        // Arrange
        var ipv6 = IPAddress.Parse("2001:db8::a");
        var ipv4 = new IPAddress([192, 0, 2, 10]);
        var port = 1024 + _fixture.Create<byte>();
        var plan = DualStackSocketsHttpHandler.PlanConnect([ipv6, ipv4]);
        var ipv4Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        await using var stream = await DualStackSocketsHttpHandler.ConnectAsync(
            plan,
            port,
            (address, _, cancellationToken) =>
            {
                if (address.Equals(ipv4))
                {
                    ipv4Started.TrySetResult(true);
                    return HangUntilCanceledAsync(cancellationToken);
                }

                return Task.FromResult<Stream>(new OakLaneStream(address));
            },
            TimeSpan.FromSeconds(5));

        // Assert
        var winner = Assert.IsType<OakLaneStream>(stream);
        Assert.Equal(ipv6, winner.Address);
        Assert.False(ipv4Started.Task.IsCompleted);
    }

    [Fact]
    public async Task ConnectAsync_WhenIpv6FailsImmediately_Ipv4Wins()
    {
        // Arrange
        var ipv6 = IPAddress.Parse("2001:db8::3");
        var ipv4 = new IPAddress([192, 0, 2, 50]);
        var port = 1024 + _fixture.Create<byte>();
        var plan = DualStackSocketsHttpHandler.PlanConnect([ipv6, ipv4]);

        // Act
        await using var stream = await DualStackSocketsHttpHandler.ConnectAsync(
            plan,
            port,
            (address, _, _) => address.Equals(ipv6)
                ? Task.FromException<Stream>(new SocketException((int)SocketError.NetworkUnreachable))
                : Task.FromResult<Stream>(new OakLaneStream(address)),
            TimeSpan.FromMilliseconds(20));

        // Assert
        var winner = Assert.IsType<OakLaneStream>(stream);
        Assert.Equal(ipv4, winner.Address);
    }

    [Fact]
    public async Task ConnectAsync_WhenBothFamiliesFail_ThrowsAggregateException()
    {
        // Arrange
        var ipv6 = IPAddress.Parse("2001:db8::2");
        var ipv4 = new IPAddress([192, 0, 2, 30]);
        var port = 1024 + _fixture.Create<byte>();
        var plan = DualStackSocketsHttpHandler.PlanConnect([ipv6, ipv4]);

        // Act
        var thrown = await Assert.ThrowsAsync<AggregateException>(() => DualStackSocketsHttpHandler.ConnectAsync(
            plan,
            port,
            (_, _, _) => Task.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused)),
            TimeSpan.FromMilliseconds(20)));

        // Assert
        Assert.All(thrown.InnerExceptions, ex => Assert.IsType<SocketException>(ex));
        Assert.Equal(2, thrown.InnerExceptions.Count);
    }

    [Fact]
    public async Task ConnectAsync_WhenOnlyIpv4_ConnectsWithoutRacingIpv6()
    {
        // Arrange
        var ipv4 = new IPAddress([192, 0, 2, 40]);
        var port = 1024 + _fixture.Create<byte>();
        var plan = DualStackSocketsHttpHandler.PlanConnect([ipv4]);
        var attempts = new ConcurrentQueue<IPAddress>();

        // Act
        await using var stream = await DualStackSocketsHttpHandler.ConnectAsync(
            plan,
            port,
            (address, _, _) =>
            {
                attempts.Enqueue(address);
                return Task.FromResult<Stream>(new OakLaneStream(address));
            },
            TimeSpan.FromSeconds(5));

        // Assert
        var winner = Assert.IsType<OakLaneStream>(stream);
        Assert.Equal(ipv4, winner.Address);
        Assert.Equal(new[] { ipv4 }, attempts);
    }

    [Fact]
    public async Task ConnectAsync_WhenNoAddresses_ThrowsHostNotFound()
    {
        // Arrange
        var port = 1024 + _fixture.Create<byte>();
        var plan = DualStackSocketsHttpHandler.PlanConnect(Array.Empty<IPAddress>());

        // Act
        var thrown = await Assert.ThrowsAsync<SocketException>(
            () => DualStackSocketsHttpHandler.ConnectAsync(
                plan,
                port,
                (_, _, _) => Task.FromResult<Stream>(new MemoryStream())));

        // Assert
        Assert.Equal(SocketError.HostNotFound, thrown.SocketErrorCode);
    }

    private static async Task<Stream> HangUntilCanceledAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private static async Task<Stream> SucceedAfterAsync(
        IPAddress address,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return new OakLaneStream(address);
    }

    private sealed class OakLaneStream : MemoryStream
    {
        public OakLaneStream(IPAddress address)
        {
            Address = address;
        }

        public IPAddress Address { get; }
    }
}
