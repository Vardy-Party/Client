namespace VardyParty.Kernel;

public class PlayerOverlayInfo
{
    public int Index { get; init; }
    public int Total { get; init; }
    public string? Channel { get; init; }
    public int? BitrateKbps { get; init; }
    public string? Resolution { get; init; }
    public double? FrameRate { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? AspectRatio { get; init; }
    public int? BufferPercent { get; init; }
    public string? M3u8Url { get; init; }
    public string? RefererUrl { get; init; }
    public string? Title { get; init; }
}
