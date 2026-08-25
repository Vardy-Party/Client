namespace VardyParty.Kernel;

/// <summary>
/// Normalizes stream URLs for deduplication by stripping auth tokens (query/fragment)
/// and canonicalizing scheme, host, port, and path.
/// </summary>
public static class StreamUrlNormalizer
{
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

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
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
}
