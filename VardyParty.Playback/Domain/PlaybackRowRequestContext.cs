using VardyParty.Kernel;
using VardyParty.Ports;

namespace VardyParty.Playback;

/// <summary>Referer and request headers the player sends for one attach.</summary>
public sealed record PlaybackRequestContext(string Referer, IReadOnlyDictionary<string, string>? Headers);

/// <summary>
/// Which Referer/headers a pool attach must send: the ones of the row whose resolved URL is
/// being attached, not the stream the player opened with. Each row's CDN checks its own
/// Referer (the probe's health check used it); the first row's Referer gets a 403
/// (Windows log 13 Nov 2026 19:41:33-19:41:40: streamhd247 and sportswatcher were healthy
/// with their own Referer and 403'd in the player, which still sent techydeals').
/// Same rule as the first open (StreamResolutionOrchestrator: Referer, else the row's page).
/// </summary>
public static class PlaybackRowRequestContext
{
    /// <summary>The pool row whose resolved URL is <paramref name="url"/>: the current row first.</summary>
    public static EnrichedStream? FindRow(IStreamSwitchingService? switching, string? url)
    {
        if (switching is null || string.IsNullOrWhiteSpace(url))
            return null;

        var current = switching.GetCurrentStream();
        if (Matches(current, url))
            return current;

        foreach (var row in switching.GetHealthyStreams())
        {
            if (Matches(row, url))
                return row;
        }

        return null;
    }

    /// <summary>Referer (the row's, else its catalog page) and the row's own headers.</summary>
    public static PlaybackRequestContext ForRow(EnrichedStream row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var referer = !string.IsNullOrWhiteSpace(row.Referer)
            ? row.Referer!
            : HeaderReferer(row.RequestHeaders) ?? row.Stream?.Url ?? string.Empty;
        return new PlaybackRequestContext(referer, row.RequestHeaders is { Count: > 0 } ? row.RequestHeaders : null);
    }

    /// <summary>
    /// The context for attaching <paramref name="url"/>: its row's, or <paramref name="fallback"/>
    /// when no pool row has that URL (a mirror retry inside the player, or no pool).
    /// </summary>
    public static PlaybackRequestContext ForUrl(IStreamSwitchingService? switching, string? url, PlaybackRequestContext fallback)
        => FindRow(switching, url) is { } row ? ForRow(row) : fallback;

    private static bool Matches(EnrichedStream? row, string url)
        => row is not null && string.Equals(row.ResolvedM3U8Url, url, StringComparison.OrdinalIgnoreCase);

    private static string? HeaderReferer(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
            return null;
        foreach (var pair in headers)
        {
            if (pair.Key.Equals("referer", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
                return pair.Value;
        }

        return null;
    }
}