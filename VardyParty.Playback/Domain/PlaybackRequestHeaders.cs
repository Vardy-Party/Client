namespace VardyParty.Playback;

/// <summary>
/// The HTTP headers a player sends for a manifest or segment: the row's own headers
/// (LocalService / harvest: Referer, Origin, Cookie, User-Agent) win, and anything they
/// leave out is filled in - a browser User-Agent always, and Referer + Origin from the
/// row's Referer when the headers carry none. Before round 12 the row headers were used
/// alone, so a row whose headers had a Cookie but no Referer (or no User-Agent) went out
/// without them, unlike the health check that accepted it.
/// </summary>
public static class PlaybackRequestHeaders
{
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    /// <param name="browserUserAgent">User-Agent filled in when the row has none
    /// (<see cref="BrowserUserAgent"/> when null; Linux passes its X11 Chrome string).</param>
    public static IReadOnlyList<KeyValuePair<string, string>> Resolve(
        string? referer,
        IReadOnlyDictionary<string, string>? rowHeaders,
        string? browserUserAgent = null)
    {
        var headers = new List<KeyValuePair<string, string>>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rowHeaders is not null)
        {
            foreach (var pair in rowHeaders)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) || !names.Add(pair.Key))
                    continue;
                headers.Add(new KeyValuePair<string, string>(pair.Key, pair.Value));
            }
        }

        if (!names.Contains("User-Agent"))
            headers.Add(new KeyValuePair<string, string>(
                "User-Agent",
                string.IsNullOrWhiteSpace(browserUserAgent) ? BrowserUserAgent : browserUserAgent));

        if (string.IsNullOrWhiteSpace(referer) || names.Contains("Referer"))
            return headers;

        headers.Add(new KeyValuePair<string, string>("Referer", referer));
        if (!names.Contains("Origin") && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            headers.Add(new KeyValuePair<string, string>("Origin", $"{refererUri.Scheme}://{refererUri.Authority}"));
        return headers;
    }

    /// <summary>
    /// The full header set for one attach (switch, revert or retry) as a case-insensitive
    /// map: the context's row headers plus the filled-in User-Agent / Referer / Origin.
    /// Android (ExoPlayer data sources) and Linux (loopback HLS proxy) send exactly this,
    /// the same rule Windows applies per segment.
    /// </summary>
    public static Dictionary<string, string> ForContext(PlaybackRequestContext context, string? browserUserAgent = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Resolve(context.Referer, context.Headers, browserUserAgent))
            map[pair.Key] = pair.Value;
        return map;
    }
}