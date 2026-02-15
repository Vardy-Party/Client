namespace VardyParty.Models;

public class StreamResponse
{
    public string Href { get; set; } = string.Empty;
    public List<Stream> Streams { get; set; } = new();
}

public class Stream
{
    public string Url { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string Reputation { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public int Ads { get; set; }
    // Optional metadata found after m3u8 parsing
    public int? BitrateKbps { get; set; }
    public string? Resolution { get; set; }
}
