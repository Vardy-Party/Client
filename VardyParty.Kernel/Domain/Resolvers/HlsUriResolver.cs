using System.Diagnostics.CodeAnalysis;

namespace VardyParty.Kernel;

/// <summary>
/// Resolves the URIs an HLS playlist lists (variant lines, segment lines,
/// <c>EXT-X-KEY</c> / <c>EXT-X-MAP</c> / <c>EXT-X-MEDIA</c> <c>URI=</c>) against the
/// playlist URL, identically on every platform.
/// <para>
/// <c>Uri.TryCreate("/hls-segments/x.ts", UriKind.Absolute, ...)</c> succeeds on
/// Unix-like runtimes (Android, Linux) as an implicit <c>file:///</c> path but fails
/// on Windows. Treating that as "already absolute" left root-relative segments
/// unresolved on Android, HttpClient threw <c>net_http_client_invalid_requesturi</c>
/// and live streams were reported <c>SegmentUnreachable</c>. Only explicit http(s)
/// URLs are absolute here; everything else is a reference relative to the playlist.
/// </para>
/// </summary>
public static class HlsUriResolver
{
    /// <summary>True for an absolute <c>http</c> or <c>https</c> URI (never <c>file</c>, never relative).</summary>
    public static bool IsHttpUrl([NotNullWhen(true)] Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>True when <paramref name="value"/> is an explicit absolute http(s) URL.</summary>
    public static bool IsHttpUrl([NotNullWhen(true)] string? value) => TryGetHttpUrl(value, out _);

    /// <summary>
    /// Parses <paramref name="value"/> only when it is an explicit absolute http(s) URL.
    /// Root-relative paths (Unix implicit <c>file:///</c>) and other schemes are rejected.
    /// </summary>
    public static bool TryGetHttpUrl(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!TryCreateExplicitAbsolute(value.Trim(), out var parsed) || !IsHttpUrl(parsed))
            return false;

        uri = parsed;
        return true;
    }

    /// <summary>
    /// <c>Uri.TryCreate(value, UriKind.Absolute)</c> without the platform-specific
    /// implicit file path parse: <c>/path</c> (Unix) and <c>C:\path</c> (Windows) are not
    /// absolute URIs here, so URL handling is the same on Android, Linux and Windows.
    /// Only strings that start with their own <c>scheme:</c> are accepted.
    /// </summary>
    public static bool TryCreateExplicitAbsolute(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
            return false;

        if (!trimmed.StartsWith(parsed.Scheme + ":", StringComparison.OrdinalIgnoreCase))
            return false;

        uri = parsed;
        return true;
    }

    /// <summary>
    /// Resolves a playlist <paramref name="reference"/> against <paramref name="playlistUri"/>.
    /// Explicit http(s) URLs are returned as-is; root-relative (<c>/seg.ts</c>), relative
    /// (<c>seg.ts</c>, <c>../seg.ts</c>), protocol-relative (<c>//cdn/seg.ts</c>) and
    /// query-only references are combined with the playlist URL (RFC 3986). Fails when
    /// the base is not http(s) or the result is not a fetchable http(s) URL.
    /// </summary>
    public static bool TryResolve(Uri? playlistUri, string? reference, [NotNullWhen(true)] out Uri? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        var trimmed = reference.Trim();
        if (TryGetHttpUrl(trimmed, out var absolute))
        {
            resolved = absolute;
            return true;
        }

        if (!IsHttpUrl(playlistUri))
            return false;

        // Explicit scheme that is not http(s) (file:, data:, skd:, ...): never fetchable.
        if (TryCreateExplicitAbsolute(trimmed, out _))
            return false;

        Uri? combined;
        if (Uri.TryCreate(trimmed, UriKind.Relative, out var relative))
        {
            if (!Uri.TryCreate(playlistUri, relative, out combined))
                return false;
        }
        else if (!Uri.TryCreate(playlistUri, trimmed, out combined))
        {
            return false;
        }

        if (!IsHttpUrl(combined))
            return false;

        resolved = combined;
        return true;
    }

    /// <inheritdoc cref="TryResolve(Uri?, string?, out Uri?)"/>
    public static bool TryResolve(string? playlistUrl, string? reference, [NotNullWhen(true)] out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        if (TryGetHttpUrl(reference, out _))
        {
            // Already absolute: keep the exact text (tokens / escaping untouched).
            resolved = reference.Trim();
            return true;
        }

        if (!TryGetHttpUrl(playlistUrl, out var baseUri)
            || !TryResolve(baseUri, reference, out Uri? combined))
        {
            return false;
        }

        resolved = combined.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// <see cref="TryResolve(string?, string?, out string?)"/>, falling back to the trimmed
    /// <paramref name="reference"/> when it cannot be resolved to an http(s) URL.
    /// </summary>
    public static string Resolve(string? playlistUrl, string reference) =>
        TryResolve(playlistUrl, reference, out string? resolved) ? resolved : reference.Trim();
}
