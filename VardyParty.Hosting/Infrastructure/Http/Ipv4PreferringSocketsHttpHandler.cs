using System.Net;
using System.Net.Sockets;

namespace VardyParty.Hosting;

/// <summary>
/// Android OkHttp (<see cref="HttpClientHandler"/>) can hang connecting to
/// Cloudflare when DNS returns AAAA first but IPv6 routing is broken. Auth0
/// already worked around this with <see cref="SocketsHttpHandler"/>; catalog
/// and stream-health HTTP must use the same path.
/// </summary>
public static class Ipv4PreferringSocketsHttpHandler
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan PerAddressTimeout = TimeSpan.FromSeconds(5);

    public static IReadOnlyList<IPAddress> OrderForConnect(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var snapshot = addresses as IPAddress[] ?? addresses.ToArray();
        var ordered = new List<IPAddress>(snapshot.Length);
        foreach (var address in snapshot)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
                ordered.Add(address);
        }

        foreach (var address in snapshot)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                ordered.Add(address);
        }

        return ordered;
    }

    public static SocketsHttpHandler Create(bool ignoreSslCertificateErrors = false)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
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
        var ordered = OrderForConnect(resolved);
        if (ordered.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        Exception? last = null;
        foreach (var address in ordered)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(PerAddressTimeout);
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cts.Token)
                    .ConfigureAwait(false);
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
}
