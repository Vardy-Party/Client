namespace VardyParty.Kernel;

/// <summary>
/// Normalizes stream URLs for deduplication by stripping auth tokens (query/fragment)
/// and canonicalizing scheme, host, port, and path.
/// </summary>
public static class StreamUrlNormalizer
{
    /// <summary>Shortest pure-digit path segment treated as an expiry (Unix seconds = 10 digits).</summary>
    private const int MinExpiryDigits = 9;

    /// <summary>Shortest hex or base64url path segment treated as a token/signature.</summary>
    private const int MinTokenLength = 32;

    public static string NormalizeForDedup(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var trimmed = value.Trim();

        static string TrimPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            if (path.Length > 1 && path.EndsWith('/')) return path.TrimEnd('/');
            return path;
        }

        if (!HlsUriResolver.TryCreateExplicitAbsolute(trimmed, out var uri))
        {
            var fragmentIndex = trimmed.IndexOf('#');
            if (fragmentIndex >= 0) trimmed = trimmed[..fragmentIndex];
            var queryIndex = trimmed.IndexOf('?');
            if (queryIndex >= 0) trimmed = trimmed[..queryIndex];
            return TrimPath(trimmed);
        }

        var path = TrimPath(uri.AbsolutePath);
        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var hasNonDefaultPort = !uri.IsDefaultPort;
        var authority = hasNonDefaultPort ? $"{host}:{uri.Port}" : host;

        return $"{scheme}://{authority}{path}";
    }

    /// <summary>
    /// Identity of the media an m3u8 URL points at, for deduplicating the healthy pool.
    /// Starts from <see cref="NormalizeForDedup"/> (query/fragment stripped, scheme+host
    /// lowercased) and also drops token-like <b>directory</b> segments that change per
    /// <c>/play</c>: pure-digit expiries (9+ digits), long hex (32+), and mixed-case
    /// base64url signatures (32+). The host and every other segment are kept, and the
    /// final segment (the playlist filename) is never dropped, so <c>premium38</c> vs
    /// <c>premium35</c> and <c>skysportsfootball-uk.m3u8</c> vs <c>skysportsmain-uk.m3u8</c>
    /// stay distinct. Returns empty for empty input.
    /// </summary>
    public static string M3u8IdentityKey(string? value)
    {
        var normalized = NormalizeForDedup(value);
        if (normalized.Length == 0) return string.Empty;

        var schemeEnd = normalized.IndexOf("://", StringComparison.Ordinal);
        var pathStart = schemeEnd >= 0
            ? normalized.IndexOf('/', schemeEnd + 3)
            : normalized.IndexOf('/');
        if (pathStart < 0) return normalized;

        var prefix = normalized[..pathStart];
        var segments = normalized[(pathStart + 1)..].Split('/');
        if (segments.Length <= 1) return normalized;

        var kept = new List<string>(segments.Length);
        for (var i = 0; i < segments.Length; i++)
        {
            var isLast = i == segments.Length - 1;
            if (!isLast && IsTokenLikeSegment(segments[i])) continue;
            kept.Add(segments[i]);
        }

        return prefix + "/" + string.Join('/', kept);
    }

    /// <summary>True for per-request token/expiry path segments (see <see cref="M3u8IdentityKey"/>).</summary>
    public static bool IsTokenLikeSegment(string? segment)
    {
        if (string.IsNullOrEmpty(segment)) return false;

        if (segment.Length >= MinExpiryDigits && segment.All(char.IsAsciiDigit)) return true;

        if (segment.Length < MinTokenLength) return false;

        if (segment.All(char.IsAsciiHexDigit)) return true;

        // base64url / signature: only token alphabet, and mixed upper+lower+digit so
        // lowercase channel slugs ("premier-league-hd-channel-number-1") are kept.
        var tokenAlphabet = segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '=' or '~' or '+');
        return tokenAlphabet
               && segment.Any(char.IsAsciiLetterUpper)
               && segment.Any(char.IsAsciiLetterLower)
               && segment.Any(char.IsAsciiDigit);
    }
}
