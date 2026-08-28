using System.Text;
using System.Text.RegularExpressions;

namespace VardyParty.Desktop.Services;

/// <summary>
/// Rewrites HLS playlist URI references so LibVLC fetches every manifest,
/// variant, key, and segment through a local referer-injecting bridge.
/// LibVLC's native HTTP stack often fails (or drops Referer) on WSL where
/// DualStack <see cref="System.Net.Http.HttpClient"/> succeeds.
/// </summary>
public static partial class HlsPlaylistProxyRewriter
{
    private static readonly Regex QuotedUriAttribute = UriAttributeRegex();

    /// <summary>
    /// Rewrite playlist body. <paramref name="toProxiedUrl"/> receives an
    /// absolute http(s) URL and returns the local bridge URL LibVLC should open.
    /// </summary>
    public static string Rewrite(string playlist, Uri playlistUri, Func<string, string> toProxiedUrl)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(playlistUri);
        ArgumentNullException.ThrowIfNull(toProxiedUrl);

        var lines = playlist.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var sb = new StringBuilder(playlist.Length + 64);

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '#')
            {
                sb.Append(RewriteTagLine(line, playlistUri, toProxiedUrl));
                continue;
            }

            var absolute = ResolveAgainst(playlistUri, line.Trim());
            sb.Append(LooksLikeHttpUrl(absolute) ? toProxiedUrl(absolute) : line);
        }

        return sb.ToString();
    }

    private static string RewriteTagLine(string line, Uri playlistUri, Func<string, string> toProxiedUrl)
    {
        if (!line.Contains("URI=", StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        return QuotedUriAttribute.Replace(line, match =>
        {
            var doubleQuoted = match.Groups[1].Success;
            var raw = doubleQuoted ? match.Groups[1].Value : match.Groups[2].Value;
            var absolute = ResolveAgainst(playlistUri, raw);
            if (!LooksLikeHttpUrl(absolute))
            {
                return match.Value;
            }

            var proxied = toProxiedUrl(absolute);
            var quote = doubleQuoted ? "\"" : "'";
            return "URI=" + quote + proxied + quote;
        });
    }

    private static string ResolveAgainst(Uri baseUri, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return reference;
        }

        if (reference.StartsWith("//", StringComparison.Ordinal))
        {
            return baseUri.Scheme + ":" + reference;
        }

        if (Uri.TryCreate(reference, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(baseUri, reference, out var combined))
        {
            return combined.ToString();
        }

        return reference;
    }

    private static bool LooksLikeHttpUrl(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"URI=(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriAttributeRegex();
}
