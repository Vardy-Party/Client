using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VardyParty.Desktop.Services;

/// <summary>
/// Local HTTP bridge so LibVLC never talks to stream CDNs directly.
/// Every playlist/segment/key request is fetched with DualStack HttpClient
/// + the stream Referer (the path that already passes health checks on WSL).
/// </summary>
internal sealed class LibVlcRefererProxy : IAsyncDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private Task? _loop;
    private string? _entryRemoteUrl;
    private string? _referer;
    private IReadOnlyDictionary<string, string>? _extraHeaders;
    private int _port;

    public LibVlcRefererProxy(HttpClient http, ILogger logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Bind a remote m3u8 as the entry playlist and return the local URL
    /// LibVLC should Play. Safe to call again to re-bind a different stream.
    /// </summary>
    public async Task<string> BindAsync(
        string remoteM3u8Url,
        string? refererUrl,
        IReadOnlyDictionary<string, string>? requestHeaders,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteM3u8Url);

        _entryRemoteUrl = remoteM3u8Url;
        _referer = refererUrl;
        _extraHeaders = requestHeaders;

        if (_loop == null)
        {
            StartListener();
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        }

        // Warm the entry path so we fail fast if DualStack+referer cannot fetch
        // (LibVLC would otherwise show a blank surface with a vague demux error).
        using var warm = await CreateUpstreamRequestAsync(remoteM3u8Url, HttpMethod.Get, range: null, cancellationToken);
        using var warmResponse = await _http.SendAsync(warm, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        warmResponse.EnsureSuccessStatusCode();

        var local = EntryUrl;
        _logger.LogInformation(
            "[LibVlcRefererProxy] Bridging {Remote} via {Local} (referer={Referer})",
            remoteM3u8Url, local, string.IsNullOrWhiteSpace(refererUrl) ? "(none)" : refererUrl);
        return local;
    }

    public string EntryUrl => $"http://127.0.0.1:{_port}/entry.m3u8";

    private void StartListener()
    {
        // Bind an ephemeral port on loopback — no URL ACL needed on WSL/Linux.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var port = Random.Shared.Next(41_000, 52_000);
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
                _port = port;
                return;
            }
            catch (HttpListenerException)
            {
                // Port busy — try another.
            }
        }

        throw new InvalidOperationException("Could not bind LibVLC referer proxy on loopback");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/entry.m3u8", StringComparison.OrdinalIgnoreCase))
            {
                var entry = _entryRemoteUrl;
                if (entry == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                await WriteProxiedAsync(context, entry, rewritePlaylist: true, cancellationToken);
                return;
            }

            if (path.Equals("/u", StringComparison.OrdinalIgnoreCase))
            {
                var encoded = context.Request.QueryString["u"];
                if (string.IsNullOrWhiteSpace(encoded) ||
                    !Uri.TryCreate(encoded, UriKind.Absolute, out var target) ||
                    (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    return;
                }

                var rewrite = LooksLikePlaylist(target);
                await WriteProxiedAsync(context, target.ToString(), rewrite, cancellationToken);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LibVlcRefererProxy] Request handler failed");
            try
            {
                context.Response.StatusCode = 502;
                context.Response.Close();
            }
            catch
            {
                // ignored
            }
        }
    }

    private async Task WriteProxiedAsync(
        HttpListenerContext context,
        string remoteUrl,
        bool rewritePlaylist,
        CancellationToken cancellationToken)
    {
        if (_seen.TryAdd(remoteUrl, 0))
        {
            _logger.LogDebug("[LibVlcRefererProxy] First fetch {Url} rewrite={Rewrite}", remoteUrl, rewritePlaylist);
        }

        var range = context.Request.Headers["Range"];
        using var request = await CreateUpstreamRequestAsync(
            remoteUrl,
            new HttpMethod(context.Request.HttpMethod),
            range,
            cancellationToken);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType != null)
        {
            context.Response.ContentType = response.Content.Headers.ContentType.ToString();
        }

        if (response.Headers.AcceptRanges.Count > 0)
        {
            context.Response.AddHeader("Accept-Ranges", string.Join(",", response.Headers.AcceptRanges));
        }

        if (response.Content.Headers.ContentRange != null)
        {
            context.Response.AddHeader("Content-Range", response.Content.Headers.ContentRange.ToString());
        }

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
        {
            _logger.LogWarning(
                "[LibVlcRefererProxy] Upstream {Status} for {Url}",
                (int)response.StatusCode, remoteUrl);
            context.Response.Close();
            return;
        }

            if (rewritePlaylist)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!body.Contains("#EXTM3U", StringComparison.Ordinal))
                {
                    // Upstream returned HTML/JSON — do not pretend it is a playlist.
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType ??= "application/octet-stream";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
                    context.Response.Close();
                    return;
                }

                var playlistUri = new Uri(remoteUrl, UriKind.Absolute);
                var rewritten = HlsPlaylistProxyRewriter.Rewrite(body, playlistUri, ToProxiedUrl);
                var payload = Encoding.UTF8.GetBytes(rewritten);
                context.Response.ContentType = "application/vnd.apple.mpegurl";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, cancellationToken);
                context.Response.Close();
                return;
            }

            // Some CDNs serve nested playlists without a .m3u8 suffix — peek.
            await using var upstream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var peek = new byte[16];
            var peeked = await ReadAtLeastAsync(upstream, peek, cancellationToken);
            if (peeked > 0 &&
                Encoding.UTF8.GetString(peek, 0, peeked).Contains("#EXTM3U", StringComparison.Ordinal))
            {
                using var full = new MemoryStream();
                full.Write(peek, 0, peeked);
                await upstream.CopyToAsync(full, cancellationToken);
                var body = Encoding.UTF8.GetString(full.ToArray());
                var playlistUri = new Uri(remoteUrl, UriKind.Absolute);
                var rewritten = HlsPlaylistProxyRewriter.Rewrite(body, playlistUri, ToProxiedUrl);
                var payload = Encoding.UTF8.GetBytes(rewritten);
                context.Response.ContentType = "application/vnd.apple.mpegurl";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, cancellationToken);
                context.Response.Close();
                return;
            }

            context.Response.SendChunked = true;
            if (peeked > 0)
            {
                await context.Response.OutputStream.WriteAsync(peek.AsMemory(0, peeked), cancellationToken);
            }

            await upstream.CopyToAsync(context.Response.OutputStream, cancellationToken);
            context.Response.Close();
        }

    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private string ToProxiedUrl(string absoluteHttpUrl) =>
        $"http://127.0.0.1:{_port}/u?u={Uri.EscapeDataString(absoluteHttpUrl)}";

    private Task<HttpRequestMessage> CreateUpstreamRequestAsync(
        string url,
        HttpMethod method,
        string? range,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");

        if (!string.IsNullOrWhiteSpace(_referer) &&
            Uri.TryCreate(_referer, UriKind.Absolute, out var refererUri))
        {
            request.Headers.Referrer = refererUri;
        }

        if (!string.IsNullOrWhiteSpace(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        if (_extraHeaders != null)
        {
            foreach (var pair in _extraHeaders)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    continue;
                }

                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        return Task.FromResult(request);
    }

    private static bool LooksLikePlaylist(Uri uri)
    {
        var path = uri.AbsolutePath;
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignored
        }

        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            _listener.Close();
        }
        catch
        {
            // ignored
        }

        if (_loop != null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignored
            }
        }

        _cts.Dispose();
    }
}
