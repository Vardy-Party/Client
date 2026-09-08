namespace VardyParty.Kernel;



public class StreamResponse

{

    public string Href { get; set; } = string.Empty;

    public List<Stream> Streams { get; set; } = new();

}



public class Stream

{

    public string Url { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    public StreamReputation Reputation { get; set; }

    public string Quality { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public int Ads { get; set; }

    /// <summary>
    /// Catalog origin for this stream link: "fb" or "mp". Prefer <see cref="ResolveCatalogSource"/> —
    /// never apply game-level sources to an individual stream.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>

    /// How the local service should resolve this stream page (v2 = multi-stream picker).

    /// </summary>

    public string ResolutionStrategy { get; set; } = string.Empty;



    /// <summary>

    /// Player label to pass as <c>?stream=</c> when resolving via local service.

    /// </summary>

    public string PlayerStream { get; set; } = string.Empty;



    /// <summary>

    /// All player labels discovered on a multi-stream page (informational).

    /// </summary>

    public List<string> PlayerStreams { get; set; } = new();



    /// <summary>

    /// ready | countdown | unknown — from API v2 metadata when stream page is not live yet.

    /// </summary>

    public string StreamStatus { get; set; } = string.Empty;



    // Optional metadata found after m3u8 parsing

    public int? BitrateKbps { get; set; }

    public string? Resolution { get; set; }



    public bool RequiresV2StreamSelection =>

        string.Equals(ResolutionStrategy, "v2", StringComparison.OrdinalIgnoreCase);



    public bool IsCountdown =>

        string.Equals(StreamStatus, "countdown", StringComparison.OrdinalIgnoreCase);



    public bool IsReadyForLocalResolution =>

        !IsCountdown && !string.IsNullOrWhiteSpace(Url);

    /// <summary>
    /// Per-stream catalog badge from URL/strategy evidence, not game-level sources.
    /// </summary>
    public string ResolveCatalogSource()
    {
        // URL host is authoritative — never trust sticky Source/strategy on FB URLs.
        // IsMpStreamUrl is badge/order only; LAN play is RequiresV2StreamSelection.
        if (IsMpStreamUrl(Url))
        {
            return "mp";
        }

        if (!string.IsNullOrWhiteSpace(Url))
        {
            return "fb";
        }

        if (RequiresV2StreamSelection)
        {
            return "mp";
        }

        return string.Equals(Source, "mp", StringComparison.OrdinalIgnoreCase) ? "mp"
            : string.Equals(Source, "fb", StringComparison.OrdinalIgnoreCase) ? "fb"
            : string.Empty;
    }

    public string CatalogSourceBadgeLabel =>
        ResolveCatalogSource() switch
        {
            "mp" => "V2",
            "fb" => "FB",
            _ => string.Empty
        };

    private static bool IsMpStreamUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        // Player CDNs persisted by the API (hostname only).
        if (host.Contains("mpoutqn", StringComparison.OrdinalIgnoreCase)
            || host.Contains("mpgreatest", StringComparison.OrdinalIgnoreCase)
            || (host.StartsWith("jack", StringComparison.OrdinalIgnoreCase)
                && host.Contains(".mp", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Schedule-catalog brand — badge/order only, not a persisted player URL.
        return host.Contains("fctv33hd", StringComparison.OrdinalIgnoreCase);
    }

}

