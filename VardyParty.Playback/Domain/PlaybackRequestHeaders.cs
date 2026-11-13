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

    public static IReadOnlyList<KeyValuePair<string, string>> Resolve(
        string? referer,
        IReadOnlyDictionary<string, string>? rowHeaders)
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
            headers.Add(new KeyValuePair<string, string>("User-Agent", BrowserUserAgent));

        if (string.IsNullOrWhiteSpace(referer) || names.Contains("Referer"))
            return headers;

        headers.Add(new KeyValuePair<string, string>("Referer", referer));
        if (!names.Contains("Origin") && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            headers.Add(new KeyValuePair<string, string>("Origin", $"{refererUri.Scheme}://{refererUri.Authority}"));
        return headers;
    }
}