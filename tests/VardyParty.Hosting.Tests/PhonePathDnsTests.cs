using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Hosting;
using Xunit;

namespace VardyParty.Hosting.Tests;

public class PhonePathDnsTests
{
    [Fact]
    public async Task ResolveAsync_LiteralIp_SkipsLookups()
    {
        var result = await PhonePathDns.ResolveAsync(
            "192.0.2.10",
            dohEnabled: true,
            doh: (_, _) => throw new InvalidOperationException("DoH must not run"),
            system: (_, _) => throw new InvalidOperationException("system DNS must not run"),
            CancellationToken.None);

        Assert.Equal(PhonePathDns.Source.Literal, result.Via);
        Assert.Equal([IPAddress.Parse("192.0.2.10")], result.Addresses);
    }

    [Fact]
    public async Task ResolveAsync_WhenDohEnabled_UsesDohFirst()
    {
        var expected = IPAddress.Parse("203.0.113.50");
        var result = await PhonePathDns.ResolveAsync(
            "player.example.test",
            dohEnabled: true,
            doh: (_, _) => Task.FromResult(new[] { expected }),
            system: (_, _) => throw new InvalidOperationException("system DNS must not run"),
            CancellationToken.None);

        Assert.Equal(PhonePathDns.Source.Doh, result.Via);
        Assert.Equal([expected], result.Addresses);
    }

    [Fact]
    public async Task ResolveAsync_WhenDohFails_UsesSystemDns()
    {
        var expected = IPAddress.Parse("198.51.100.8");
        var result = await PhonePathDns.ResolveAsync(
            "player.example.test",
            dohEnabled: true,
            doh: (_, _) => throw new SocketException((int)SocketError.TimedOut),
            system: (_, _) => Task.FromResult(new[] { expected }),
            CancellationToken.None);

        Assert.Equal(PhonePathDns.Source.System, result.Via);
        Assert.Equal([expected], result.Addresses);
    }

    [Fact]
    public async Task ResolveAsync_WhenDohDisabled_UsesSystemDns()
    {
        var expected = IPAddress.Parse("198.51.100.9");
        var result = await PhonePathDns.ResolveAsync(
            "player.example.test",
            dohEnabled: false,
            doh: (_, _) => throw new InvalidOperationException("DoH must not run"),
            system: (_, _) => Task.FromResult(new[] { expected }),
            CancellationToken.None);

        Assert.Equal(PhonePathDns.Source.System, result.Via);
        Assert.Equal([expected], result.Addresses);
    }
}
