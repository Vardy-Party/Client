using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VardyParty.Hosting;
using VardyParty.Ports;
using Xunit;

namespace VardyParty.Hosting.Tests;

public class SystemThenDohHostNameResolverTests
{
    [Fact]
    public async Task ResolveAsync_WhenSystemDnsWorks_DoesNotCallDoh()
    {
        // Arrange
        var prefs = new Mock<IDnsPreferencesStore>();
        prefs.Setup(p => p.LoadDnsOverHttpsFallbackEnabled()).Returns(true);
        var doh = new Mock<IDnsOverHttpsClient>(MockBehavior.Strict);
        var sut = new SystemThenDohHostNameResolver(prefs.Object, doh.Object);

        // Act
        var addresses = await sut.ResolveAsync("localhost");

        // Assert
        Assert.NotEmpty(addresses);
        doh.Verify(
            d => d.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_WhenSystemDnsFails_AndDohEnabled_UsesCloudflare()
    {
        // Arrange
        var prefs = new Mock<IDnsPreferencesStore>();
        prefs.Setup(p => p.LoadDnsOverHttpsFallbackEnabled()).Returns(true);
        var expected = IPAddress.Parse("203.0.113.50");
        var doh = new Mock<IDnsOverHttpsClient>();
        doh.Setup(d => d.ResolveAsync("no.such.host.vardyparty.test", It.IsAny<CancellationToken>()))
            .ReturnsAsync([expected]);
        var sut = new SystemThenDohHostNameResolver(prefs.Object, doh.Object);

        // Act
        var addresses = await sut.ResolveAsync("no.such.host.vardyparty.test");

        // Assert
        Assert.Equal([expected], addresses);
        doh.Verify(
            d => d.ResolveAsync("no.such.host.vardyparty.test", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_WhenSystemDnsFails_AndDohDisabled_Throws()
    {
        // Arrange
        var prefs = new Mock<IDnsPreferencesStore>();
        prefs.Setup(p => p.LoadDnsOverHttpsFallbackEnabled()).Returns(false);
        var doh = new Mock<IDnsOverHttpsClient>(MockBehavior.Strict);
        var sut = new SystemThenDohHostNameResolver(prefs.Object, doh.Object);

        // Act
        var ex = await Assert.ThrowsAnyAsync<SocketException>(
            () => sut.ResolveAsync("no.such.host.vardyparty.test"));

        // Assert
        Assert.NotNull(ex);
        doh.Verify(
            d => d.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_LiteralIp_ReturnsWithoutLookup()
    {
        // Arrange
        var prefs = new Mock<IDnsPreferencesStore>(MockBehavior.Strict);
        var doh = new Mock<IDnsOverHttpsClient>(MockBehavior.Strict);
        var sut = new SystemThenDohHostNameResolver(prefs.Object, doh.Object);

        // Act
        var addresses = await sut.ResolveAsync("192.0.2.10");

        // Assert
        Assert.Equal([IPAddress.Parse("192.0.2.10")], addresses);
    }

    [Fact]
    public async Task ResolveAsync_WhenDohThrows_SurfacesSocketExceptionNotAggregate()
    {
        // Arrange
        var prefs = new Mock<IDnsPreferencesStore>();
        prefs.Setup(p => p.LoadDnsOverHttpsFallbackEnabled()).Returns(true);
        var doh = new Mock<IDnsOverHttpsClient>();
        doh.Setup(d => d.ResolveAsync("no.such.host.vardyparty.test", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AggregateException(
                new SocketException((int)SocketError.TimedOut),
                new SocketException((int)SocketError.TimedOut)));
        var sut = new SystemThenDohHostNameResolver(prefs.Object, doh.Object);

        // Act
        var ex = await Assert.ThrowsAnyAsync<SocketException>(
            () => sut.ResolveAsync("no.such.host.vardyparty.test"));

        // Assert — callers must see SocketException, not AggregateException
        Assert.NotNull(ex);
        Assert.IsNotType<AggregateException>(ex);
    }
}
