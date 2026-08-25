using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using AutoFixture;
using VardyParty.Hosting;
using Xunit;

namespace VardyParty.Tests;

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
}
