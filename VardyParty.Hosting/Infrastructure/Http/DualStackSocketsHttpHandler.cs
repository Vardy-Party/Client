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

    public static SocketsHttpHandler Create(
        bool ignoreSslCertificateErrors = false,
        bool useCookies = true)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = useCookies,
            ConnectCallback = ConnectAsync
        };

        if (ignoreSslCertificateErrors)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        }

        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var resolved = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        var plan = PlanConnect(resolved);
        var port = context.DnsEndPoint.Port;

        if (plan.Ipv6.Count == 0 && plan.Ipv4.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        if (plan.Ipv4.Count == 0)
            return await ConnectSequentialAsync(plan.Ipv6, port, cancellationToken).ConfigureAwait(false);

        if (plan.Ipv6.Count == 0)
            return await ConnectSequentialAsync(plan.Ipv4, port, cancellationToken).ConfigureAwait(false);

        return await ConnectHappyEyeballsAsync(plan, port, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Stream> ConnectHappyEyeballsAsync(
        ConnectPlan plan,
        int port,
        CancellationToken cancellationToken)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ipv6Task = ConnectSequentialAsync(plan.Ipv6, port, raceCts.Token);
        var ipv4Task = ConnectSequentialAfterDelayAsync(
            plan.Ipv4,
            port,
            HappyEyeballsResolutionDelay,
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
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return await ConnectSequentialAsync(addresses, port, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Stream> ConnectSequentialAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
            catch (Exception ex)
            {
                socket.Dispose();
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
