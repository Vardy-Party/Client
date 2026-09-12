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
    /// Per-stream catalog badge from <see cref="ResolutionStrategy"/> / <see cref="Source"/> —
    /// not URL host sniffing.
    /// </summary>
    public string ResolveCatalogSource()
    {
        if (RequiresV2StreamSelection
            || string.Equals(Source, "mp", StringComparison.OrdinalIgnoreCase))
        {
            return "mp";
        }

        if (string.Equals(ResolutionStrategy, "v1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Source, "fb", StringComparison.OrdinalIgnoreCase))
        {
            return "fb";
        }

        return string.IsNullOrWhiteSpace(Source) ? string.Empty : Source.Trim().ToLowerInvariant();
    }

    public string CatalogSourceBadgeLabel =>
        ResolveCatalogSource() switch
        {
            "mp" => "V2",
            "fb" => "FB",
            _ => string.Empty
        };

}

