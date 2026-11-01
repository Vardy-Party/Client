using System;

namespace VardyParty.Playback;

/// <summary>
/// A live player reloads its playlist on the URL it was given. When that URL is about to
/// expire, the reload is answered with the new token instead of attaching a new media source
/// (which clears the picture). Only the same playlist (scheme, host, path) is rewritten.
/// Segment URLs are left alone.
/// </summary>
public static class PlaylistReloadRedirect
{
    /// <summary>
    /// The URL to fetch, or null when <paramref name="requestUrl"/> should be fetched as-is.
    /// </summary>
    public static string? Rewrite(string? requestUrl, string? playingUrl, string? nextUrl)
    {
        if (string.IsNullOrWhiteSpace(requestUrl)
            || string.IsNullOrWhiteSpace(playingUrl)
            || string.IsNullOrWhiteSpace(nextUrl))
        {
            return null;
        }

        if (string.Equals(requestUrl, nextUrl, StringComparison.Ordinal))
            return null;

        if (!SamePlaylist(requestUrl, playingUrl))
            return null;

        return nextUrl;
    }

    private static bool SamePlaylist(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a)
            || !Uri.TryCreate(right, UriKind.Absolute, out var b))
        {
            return false;
        }

        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port
            && string.Equals(a.AbsolutePath, b.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }
}
