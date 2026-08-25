namespace VardyParty.Models;

public class PlaybackMetrics
{
    public bool IsBuffering { get; set; }
    public int? BitrateKbps { get; set; }
    public (int Width, int Height)? Resolution { get; set; }

    // Video metadata - populated once on first playback, then remains available
    public string? VideoCodec { get; set; } // e.g., "H.264", "H.265"
    public string? AudioCodec { get; set; } // e.g., "AAC", "AC-3"
    public int? Framerate { get; set; } // e.g., 30, 60
}
