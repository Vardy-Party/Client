using System.Text;
using System.Text.RegularExpressions;

namespace VardyParty.Linux.Services;

/// <summary>
/// Rewrites HLS playlists so every media/variant/key URI is fetched through
/// a loopback proxy. LibVLC then only talks to 127.0.0.1; .NET owns CDN I/O.
/// </summary>
public static class LinuxHlsPlaylistRewriter
{
    private static readonly Regex TagUri = new(
        @"URI=([""'])(?<uri>.*?)\1",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool LooksLikePlaylist(ReadOnlySpan<byte> body)
    {
        var start = 0;
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
            start = 3;
        while (start < body.Length && body[start] <= 0x20)
            start++;

        ReadOnlySpan<byte> marker = "#EXTM3U"u8;
        return body.Length - start >= marker.Length && body[start..].StartsWith(marker);
    }

    public static bool LooksLikePlaylist(string text) =>
        text.AsSpan().TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);

    /// <summary>
    /// Replace decoy playlist-relative URI lines with LocalService-captured
    /// absolute segment URLs. Leaves master playlists and already-rewritten
    /// (<c>_s2=</c> / kdns) lines alone.
    /// </summary>
    public static string SubstitutePlayableSegments(string playlist, IReadOnlyList<string>? absoluteUrls)
    {
        if (string.IsNullOrEmpty(playlist) || absoluteUrls is not { Count: > 0 })
            return playlist;

        if (playlist.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            return playlist;

        var usable = new List<string>(absoluteUrls.Count);
        foreach (var url in absoluteUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
                continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            usable.Add(url);
        }

        if (usable.Count == 0)
            return playlist;

        var sb = new StringBuilder(playlist.Length + 256);
        var span = playlist.AsSpan();
        var i = 0;
        var next = 0;
        while (i < span.Length)
        {
            var lineStart = i;
            while (i < span.Length && span[i] != '\n' && span[i] != '\r')
                i++;
            var line = span[lineStart..i];
            var newlineStart = i;
            if (i < span.Length && span[i] == '\r')
                i++;
            if (i < span.Length && span[i] == '\n')
                i++;
            var newline = span[newlineStart..i];

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                sb.Append(line);
                sb.Append(newline);
                continue;
            }

            var uriLine = trimmed.ToString();
            if (IsPlayableRewrittenUri(uriLine))
            {
                sb.Append(line);
            }
            else
            {
                sb.Append(usable[next % usable.Count]);
                next++;
            }

            sb.Append(newline);
        }

        return sb.ToString();
    }

    public static bool IsPlayableRewrittenUri(string uri) =>
        uri.Contains("_s2=", StringComparison.OrdinalIgnoreCase)
        || uri.Contains("kdns.fr", StringComparison.OrdinalIgnoreCase);

    public static string Rewrite(string playlist, string playlistUrl, string proxyPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyPrefix);

        if (!Uri.TryCreate(playlistUrl, UriKind.Absolute, out var baseUri))
            throw new ArgumentException("playlistUrl must be an absolute URI.", nameof(playlistUrl));

        var prefix = proxyPrefix.EndsWith('/') ? proxyPrefix : proxyPrefix + "/";
        var sb = new StringBuilder(playlist.Length + 256);
        var span = playlist.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            var lineStart = i;
            while (i < span.Length && span[i] != '\n' && span[i] != '\r')
                i++;
            var line = span[lineStart..i];
            var newlineStart = i;
            if (i < span.Length && span[i] == '\r')
                i++;
            if (i < span.Length && span[i] == '\n')
                i++;
            var newline = span[newlineStart..i];

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                sb.Append(newline);
                continue;
            }

            if (trimmed[0] == '#')
            {
                sb.Append(RewriteTagLine(trimmed.ToString(), baseUri, prefix));
                sb.Append(newline);
                continue;
            }

            sb.Append(ToProxyUrl(Resolve(baseUri, trimmed.ToString()), prefix));
            sb.Append(newline);
        }

        return sb.ToString();
    }

    public static string ToProxyUrl(string absoluteUrl, string proxyPrefix)
    {
        var prefix = proxyPrefix.EndsWith('/') ? proxyPrefix : proxyPrefix + "/";
        return prefix + EncodeTarget(absoluteUrl);
    }

    public static bool TryDecodeTarget(string token, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var pad = token.Length % 4;
            if (pad != 0)
                token += new string('=', 4 - pad);
            var bytes = Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/'));
            url = Encoding.UTF8.GetString(bytes);
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
        catch
        {
            return false;
        }
    }

    public static string EncodeTarget(string absoluteUrl)
    {
        var bytes = Encoding.UTF8.GetBytes(absoluteUrl);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string RewriteTagLine(string line, Uri baseUri, string proxyPrefix) =>
        TagUri.Replace(line, match =>
        {
            var original = match.Groups["uri"].Value;
            if (string.IsNullOrWhiteSpace(original))
                return match.Value;
            var quote = match.Groups[1].Value;
            return $"URI={quote}{ToProxyUrl(Resolve(baseUri, original), proxyPrefix)}{quote}";
        });

    private static string Resolve(Uri baseUri, string reference) =>
        Uri.TryCreate(baseUri, reference, out var absolute)
            ? absolute.AbsoluteUri
            : reference;
}
