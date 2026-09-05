using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace VardyParty.Hosting;

/// <summary>
/// Cloudflare DNS-over-HTTPS (1.1.1.1) using the JSON API. Connects to the
/// literal address so resolution works when system DNS cannot name hosts.
/// </summary>
public sealed class CloudflareDnsOverHttpsClient : IDnsOverHttpsClient, IDisposable
{
    public static readonly IPAddress ResolverAddress = IPAddress.Parse("1.1.1.1");
    public const string ResolverHostName = "cloudflare-dns.com";

    private readonly HttpClient _http;
    private readonly ILogger<CloudflareDnsOverHttpsClient>? _logger;

    public CloudflareDnsOverHttpsClient(ILogger<CloudflareDnsOverHttpsClient>? logger = null)
    {
        _logger = logger;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = static async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ResolverAddress, 443), cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{ResolverHostName}/"),
            Timeout = TimeSpan.FromSeconds(8)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/dns-json");
    }

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        var aTask = QueryAsync(host, DnsRecordType.A, cancellationToken);
        var aaaaTask = QueryAsync(host, DnsRecordType.AAAA, cancellationToken);
        await Task.WhenAll(aTask, aaaaTask).ConfigureAwait(false);

        var addresses = new List<IPAddress>();
        addresses.AddRange(aTask.Result);
        addresses.AddRange(aaaaTask.Result);
        return addresses.ToArray();
    }

    private async Task<IPAddress[]> QueryAsync(
        string host,
        DnsRecordType type,
        CancellationToken cancellationToken)
    {
        var path = $"dns-query?name={Uri.EscapeDataString(host)}&type={(int)type}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<DnsJsonResponse>(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (payload is null || payload.Status != 0 || payload.Answer is null || payload.Answer.Count == 0)
            return [];

        var results = new List<IPAddress>();
        foreach (var answer in payload.Answer)
        {
            if (answer.Type != (int)type || string.IsNullOrWhiteSpace(answer.Data))
                continue;
            if (IPAddress.TryParse(answer.Data, out var address))
                results.Add(address);
        }

        if (results.Count > 0)
        {
            _logger?.LogDebug(
                "[DoH] Cloudflare resolved {Host} type={Type} -> {Addresses}",
                host,
                type,
                string.Join(", ", results));
        }

        return results.ToArray();
    }

    public void Dispose() => _http.Dispose();

    private enum DnsRecordType
    {
        A = 1,
        AAAA = 28
    }

    private sealed class DnsJsonResponse
    {
        [JsonPropertyName("Status")]
        public int Status { get; set; }

        [JsonPropertyName("Answer")]
        public List<DnsJsonAnswer>? Answer { get; set; }
    }

    private sealed class DnsJsonAnswer
    {
        [JsonPropertyName("type")]
        public int Type { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }
}
