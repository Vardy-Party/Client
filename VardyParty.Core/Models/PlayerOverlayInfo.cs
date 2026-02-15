using System;

namespace VardyParty.Models
{
    // DTO for rich overlay information shown by native player
    public class PlayerOverlayInfo
    {
        public int Index { get; init; }
        public int Total { get; init; }
        public string? Channel { get; init; }
        public int? BitrateKbps { get; init; } = null;
        public string? Resolution { get; init; } = null;
        public double? FrameRate { get; init; } = null;
        public string? VideoCodec { get; init; } = null;
        public string? AudioCodec { get; init; } = null;
        public string? AspectRatio { get; init; } = null;
        // Buffer percent for current playback
        public int? BufferPercent { get; init; } = null;
        // Source URL and referer for diagnostics/overlay display
        public string? M3u8Url { get; init; }
        public string? RefererUrl { get; init; }
        public string? Title { get; init; }
    }
}
