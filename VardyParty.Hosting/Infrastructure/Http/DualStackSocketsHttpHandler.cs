using System.Net;
using System.Net.Sockets;

namespace VardyParty.Hosting;

/// <summary>
/// Managed <see cref="SocketsHttpHandler"/> for internet HTTP. Android
/// <see cref="HttpClientHandler"/> is OkHttp, which can hang on a black-holed
/// address family. This factory does not prefer IPv4 or IPv6: connect races
/// both families (RFC 8305 Happy Eyeballs). Callers must not sort or filter
/// addresses. LAN clients should keep the default factory handler.
/// </summary>
public static class DualStackSocketsHttpHandler
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PerAddressTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HappyEyeballsResolutionDelay = TimeSpan.FromMilliseconds(250);

    public readonly record struct ConnectPlan(
        IReadOnlyList<IPAddress> Ipv6,
        IReadOnlyList<IPAddress> Ipv4);

    public delegate Task<Stream> AddressConnect(
        IPAddress address,
        int port,
        CancellationToken cancellationToken);

    public static ConnectPlan PlanConnect(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var ipv6 = new List<IPAddress>();
        var ipv4 = new List<IPAddress>();
        foreach (var address in addresses)
        {
            switch (address.AddressFamily)
            {
                case AddressFamily.InterNetworkV6:
                    ipv6.Add(address);
                    break;
                case AddressFamily.InterNetwork:
                    ipv4.Add(address);
                    break;
            }
        }

        return new ConnectPlan(ipv6, ipv4);
    }

    /// <summary>
    /// WSL and some resolvers return AAAA only while A records exist. Happy
    /// Eyeballs never starts, then IPv6 black-holes for <see cref="PerAddressTimeout"/>.
    /// Merge A records when the first lookup had no IPv4.
    /// </summary>
    public static ConnectPlan WithSupplementalIpv4(ConnectPlan plan, IEnumerable<IPAddress> extraIpv4)
    {
        ArgumentNullException.ThrowIfNull(extraIpv4);

        if (plan.Ipv4.Count > 0)
            return plan;

        var ipv4 = extraIpv4
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .ToList();
        return ipv4.Count == 0 ? plan : new ConnectPlan(plan.Ipv6, ipv4);
    }

    public static SocketsHttpHandler Create(
        bool ignoreSslCertificateErrors = false,
        bool useCookies = true,
        IHostNameResolver? hostNameResolver = null)
    {
        var resolver = hostNameResolver ?? SystemDnsHostNameResolver.Instance;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = useCookies,
            ConnectCallback = (context, cancellationToken) =>
                ConnectViaDnsAsync(context, resolver, cancellationToken)
        };

        if (ignoreSslCertificateErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        return handler;
    }

    /// <summary>
    /// Races or sequences the planned addresses using <paramref name="connect"/>.
    /// Production uses sockets; tests inject connect so IPv6 hang vs IPv4 win
    /// can be asserted without opening a real TCP socket.
    /// </summary>
    public static Task<Stream> ConnectAsync(
        ConnectPlan plan,
        int port,
        AddressConnect connect,
        TimeSpan? resolutionDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connect);

        if (plan.Ipv6.Count == 0 && plan.Ipv4.Count == 0)
            return Task.FromException<Stream>(new SocketException((int)SocketError.HostNotFound));

        if (plan.Ipv4.Count == 0)
            return ConnectSequentialAsync(plan.Ipv6, port, connect, cancellationToken);

        if (plan.Ipv6.Count == 0)
            return ConnectSequentialAsync(plan.Ipv4, port, connect, cancellationToken);

        return ConnectHappyEyeballsAsync(
            plan,
            port,
            connect,
            resolutionDelay ?? HappyEyeballsResolutionDelay,
            cancellationToken);
    }

    private static async ValueTask<Stream> ConnectViaDnsAsync(
        SocketsHttpConnectionContext context,
        IHostNameResolver hostNameResolver,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var resolved = await hostNameResolver.ResolveAsync(host, cancellationToken)
            .ConfigureAwait(false);
        var plan = PlanConnect(resolved);
        if (plan.Ipv4.Count == 0)
        {
            try
            {
                var ipv4Only = await Dns.GetHostAddressesAsync(
                        host,
                        AddressFamily.InterNetwork,
                        cancellationToken)
                    .ConfigureAwait(false);
                plan = WithSupplementalIpv4(plan, ipv4Only);
            }
            catch (SocketException)
            {
                // No A records; keep the AAAA-only plan.
            }
        }

        return await ConnectAsync(plan, context.DnsEndPoint.Port, ConnectSocketAsync, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>System DNS only — used when no DoH resolver is registered.</summary>
    private sealed class SystemDnsHostNameResolver : IHostNameResolver
    {
        public static readonly SystemDnsHostNameResolver Instance = new();

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
            Dns.GetHostAddressesAsync(host, cancellationToken);
    }

    private static async Task<Stream> ConnectSocketAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(PerAddressTimeout);
            await socket.ConnectAsync(new IPEndPoint(address, port), cts.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<Stream> ConnectHappyEyeballsAsync(
        ConnectPlan plan,
        int port,
        AddressConnect connect,
        TimeSpan resolutionDelay,
        CancellationToken cancellationToken)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ipv6Task = ConnectSequentialAsync(plan.Ipv6, port, connect, raceCts.Token);
        var ipv4Task = ConnectSequentialAfterDelayAsync(
            plan.Ipv4,
            port,
            connect,
            resolutionDelay,
            raceCts.Token);

        var first = await Task.WhenAny(ipv6Task, ipv4Task).ConfigureAwait(false);
        if (first.IsCompletedSuccessfully)
        {
            raceCts.Cancel();
            var loser = first == ipv6Task ? ipv4Task : ipv6Task;
            _ = DisposeLoserAsync(loser);
            return first.Result;
        }

        var remaining = first == ipv6Task ? ipv4Task : ipv6Task;
        try
        {
            return await remaining.ConfigureAwait(false);
        }
        catch (Exception remainingException)
        {
            if (first.Exception is not null)
                throw new AggregateException(first.Exception.InnerExceptions.Append(remainingException));

            throw;
        }
    }

    private static async Task<Stream> ConnectSequentialAfterDelayAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        AddressConnect connect,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return await ConnectSequentialAsync(addresses, port, connect, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Stream> ConnectSequentialAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        AddressConnect connect,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await connect(address, port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static async Task DisposeLoserAsync(Task<Stream> loser)
    {
        try
        {
            var stream = await loser.ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Canceled or failed; the winning connection is already in use.
        }
    }
}
