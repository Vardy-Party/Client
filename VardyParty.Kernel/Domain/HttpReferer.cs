namespace VardyParty.Kernel;

/// <summary>
/// Sanitizes Referer and Origin values used for health checks and playback
/// requests, identically on every platform.
/// </summary>
public static class HttpReferer
{
    /// <summary>
    /// Returns <paramref name="value"/> when it is an explicit http(s) URL;
    /// otherwise <paramref name="fallback"/> when that is http(s); otherwise
    /// <see langword="null"/>. Drops <c>chrome-error://</c>, <c>about:</c>,
    /// <c>data:</c>, <c>blob:</c>, <c>chrome://</c> and other non-http schemes.
    /// </summary>
    public static string? Sanitize(string? value, string? fallback = null)
    {
        var trimmed = value?.Trim();
        if (HlsUriResolver.IsHttpUrl(trimmed))
            return trimmed;

        var fallbackTrimmed = fallback?.Trim();
        if (HlsUriResolver.IsHttpUrl(fallbackTrimmed))
            return fallbackTrimmed;

        return null;
    }
}