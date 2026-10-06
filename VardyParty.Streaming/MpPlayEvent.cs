namespace VardyParty.Streaming;

/// <summary>
/// One NDJSON line from a streaming <c>POST /mp</c>.
/// </summary>
public sealed class MpPlayEvent
{
    public string Type { get; set; } = "";

    public int? StatusCode { get; set; }

    public List<string>? Streams { get; set; }

    public string? SelectedStream { get; set; }

    public string? Label { get; set; }

    public string? Url { get; set; }

    public Dictionary<string, string>? RequestHeaders { get; set; }

    public List<string>? RewrittenSegments { get; set; }

    public string? Error { get; set; }
}
