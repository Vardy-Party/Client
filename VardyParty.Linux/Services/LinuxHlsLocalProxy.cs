using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VardyParty.Linux.Services;

/// <summary>
/// Loopback HLS front for LibVLC. Playlists and segments are fetched with the
/// DualStack / DoH <see cref="PlaybackHttpClients.Media"/> client; LibVLC only
/// opens <c>http://127.0.0.1:port/...</c>.
/// </summary>
public sealed class LinuxHlsLocalProxy : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyDictionary<string, string>? _headers;
    private string? _referer;
    private Func<string, string>? _playlistTransform;
    private IReadOnlyList<string>? _rewrittenSegments;
    private Task? _loop;
    private int _port;
    private bool _started;

    public LinuxHlsLocalProxy(HttpClient http, ILogger? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
    }

    public int Port => _port;

    public string Prefix => $"http://127.0.0.1:{_port}/u/";

    public bool TryStart()
    {
        if (_started)
            return true;

        try
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
            _loop = Task.Run(ListenLoopAsync);
            _started = true;
            _logger?.LogInformation("[LinuxHlsProxy] Listening on http://127.0.0.1:{Port}/", _port);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[LinuxHlsProxy] Failed to start loopback listener");
            return false;
        }
    }

    public void SetPlaybackHeaders(IReadOnlyDictionary<string, string>? headers, string? referer)
    {
        _headers = headers;
        _referer = referer;
    }

    public void SetPlaylistTransform(Func<string, string>? transform) =>
        _playlistTransform = transform;

    public void SetRewrittenSegments(IReadOnlyList<string>? urls) =>
        _rewrittenSegments = urls;

    /// <summary>Local playlist URI LibVLC should open (IPv4 loopback, never localhost).</summary>
    public Uri Wrap(string originUrl)
    {
        if (!Uri.TryCreate(originUrl, UriKind.Absolute, out var origin)
            || (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("originUrl must be an absolute http(s) URI.", nameof(originUrl));
        }

        return new Uri(LinuxHlsPlaylistRewriter.ToProxyUrl(origin.AbsoluteUri, Prefix));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        _cts.Dispose();
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        string? target = null;
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (!path.StartsWith("/u/", StringComparison.Ordinal)
                || !LinuxHlsPlaylistRewriter.TryDecodeTarget(path[3..], out var decoded))
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            target = decoded;

            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            ApplyHeaders(request);
            _logger?.LogDebug("[LinuxHlsProxy] GET {Target}", target);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                _cts.Token).ConfigureAwait(false);

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            await using var upstream = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
            var peek = new byte[16];
            var peeked = await ReadAtLeastAsync(upstream, peek, _cts.Token).ConfigureAwait(false);

            var isPlaylist = mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                             || mediaType.Contains("m3u8", StringComparison.OrdinalIgnoreCase)
                             || LinuxHlsPlaylistRewriter.LooksLikePlaylist(peek.AsSpan(0, peeked));

            ctx.Response.StatusCode = (int)response.StatusCode;
            if (isPlaylist)
            {
                var rest = await ReadRemainingAsync(upstream, _cts.Token).ConfigureAwait(false);
                var body = new byte[peeked + rest.Length];
                Buffer.BlockCopy(peek, 0, body, 0, peeked);
                Buffer.BlockCopy(rest, 0, body, peeked, rest.Length);
                var text = Encoding.UTF8.GetString(body);
                if (_playlistTransform is not null)
                {
                    var transformed = _playlistTransform(text);
                    if (!string.Equals(transformed, text, StringComparison.Ordinal))
                    {
                        _logger?.LogInformation("[LinuxHlsProxy] Applied transport playlist rewrite");
                        text = transformed;
                    }
                }

                var substituted = LinuxHlsPlaylistRewriter.SubstitutePlayableSegments(text, _rewrittenSegments);
                if (!string.Equals(substituted, text, StringComparison.Ordinal))
                {
                    _logger?.LogInformation("[LinuxHlsProxy] Substituted captured rewritten segment URLs");
                    text = substituted;
                }

                var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? target;
                var rewritten = LinuxHlsPlaylistRewriter.Rewrite(text, finalUrl, Prefix);
                var bytes = Encoding.UTF8.GetBytes(rewritten);
                ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                return;
            }

            ctx.Response.ContentType = string.IsNullOrWhiteSpace(mediaType)
                ? "video/MP2T"
                : mediaType;
            if (peeked > 0)
                await ctx.Response.OutputStream.WriteAsync(peek.AsMemory(0, peeked), _cts.Token).ConfigureAwait(false);
            await upstream.CopyToAsync(ctx.Response.OutputStream, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "[LinuxHlsProxy] Request failed for {Target}", target);
            try { ctx.Response.StatusCode = 502; } catch { /* ignore */ }
        }
        finally
        {
            try { ctx.Response.OutputStream.Close(); } catch { /* ignore */ }
            try { ctx.Response.Close(); } catch { /* ignore */ }
        }
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        if (_headers is { Count: > 0 })
        {
            foreach (var pair in _headers)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                    request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }

            return;
        }

        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        if (string.IsNullOrWhiteSpace(_referer) || !Uri.TryCreate(_referer, UriKind.Absolute, out var refererUri))
            return;

        request.Headers.TryAddWithoutValidation("Referer", _referer);
        request.Headers.TryAddWithoutValidation("Origin", $"{refererUri.Scheme}://{refererUri.Authority}");
    }

    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
                break;
            read += n;
        }

        return read;
    }

    private static async Task<byte[]> ReadRemainingAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }
}
