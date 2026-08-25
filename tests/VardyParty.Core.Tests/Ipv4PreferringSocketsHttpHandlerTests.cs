using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using AutoFixture;
using VardyParty.Hosting;
using Xunit;

namespace VardyParty.Core.Tests;

public class Ipv4PreferringSocketsHttpHandlerTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void OrderForConnect_PlacesIPv4AheadOfIPv6()
    {
        // Arrange
        var ipv6First = IPAddress.Parse("2001:db8:85a3::8a2e:370:7334");
        var ipv4 = new IPAddress([192, 0, 2, _fixture.Create<byte>()]);
        var ipv6Second = IPAddress.Parse("2001:db8::1");
        var addresses = new[] { ipv6First, ipv4, ipv6Second };

        // Act
        var ordered = Ipv4PreferringSocketsHttpHandler.OrderForConnect(addresses);

        // Assert
        Assert.Equal([ipv4, ipv6First, ipv6Second], ordered.ToArray());
    }

    [Fact]
    public void OrderForConnect_PreservesRelativeOrderWithinAddressFamily()
    {
        // Arrange
        var ipv4North = new IPAddress([192, 0, 2, 10]);
        var ipv4South = new IPAddress([192, 0, 2, 20]);
        var ipv6East = IPAddress.Parse("2001:db8::10");
        var ipv6West = IPAddress.Parse("2001:db8::20");
        var addresses = new[] { ipv6East, ipv4North, ipv6West, ipv4South };

        // Act
        var ordered = Ipv4PreferringSocketsHttpHandler.OrderForConnect(addresses);

        // Assert
        Assert.Equal([ipv4North, ipv4South, ipv6East, ipv6West], ordered.ToArray());
    }

    [Fact]
    public void OrderForConnect_WhenOnlyIPv6_KeepsIPv6Addresses()
    {
        // Arrange
        var addresses = new List<IPAddress>
        {
            IPAddress.Parse("2001:db8::a"),
            IPAddress.Parse("2001:db8::b")
        };

        // Act
        var ordered = Ipv4PreferringSocketsHttpHandler.OrderForConnect(addresses);

        // Assert
        Assert.Equal(addresses, ordered);
    }

    [Fact]
    public void Create_ConfiguresConnectCallbackAndTimeouts()
    {
        // Arrange
        const bool ignoreSsl = false;

        // Act
        using var handler = Ipv4PreferringSocketsHttpHandler.Create(ignoreSsl);

        // Assert
        Assert.NotNull(handler.ConnectCallback);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Equal(Ipv4PreferringSocketsHttpHandler.ConnectTimeout, handler.ConnectTimeout);
        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate, handler.AutomaticDecompression);
    }

    [Fact]
    public void Create_WhenIgnoreSsl_AcceptsNameMismatch()
    {
        // Arrange
        const bool ignoreSsl = true;

        // Act
        using var handler = Ipv4PreferringSocketsHttpHandler.Create(ignoreSsl);

        // Assert
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.True(handler.SslOptions.RemoteCertificateValidationCallback(
            new object(),
            null,
            null,
            SslPolicyErrors.RemoteCertificateNameMismatch));
    }
}
