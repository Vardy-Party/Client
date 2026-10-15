namespace VardyParty.Playback;

/// <summary>
/// Media Foundation content types for an HLS manifest or segment.
/// Callers pass the header media type (no parameters). A full Content-Type
/// value is still safe: parameters after ';' are ignored.
/// </summary>
internal static class PlaybackContentTypes
{
    internal const string MpegUrl = "application/vnd.apple.mpegurl";
    internal const string MpegTs = "video/MP2T";
    internal const string OctetStream = "application/octet-stream";

    public static string Resolve(string? contentType, string? path, PlaybackResourceKind kind)
    {
        var mediaType = MediaTypeOnly(contentType);
        if (kind == PlaybackResourceKind.Manifest)
            return MpegUrl;

        if (kind is PlaybackResourceKind.MediaSegment or PlaybackResourceKind.InitializationSegment
            && !IsFmp4Path(path)
            && ShouldRewriteSegmentToTransportStream(mediaType, path))
        {
            return MpegTs;
        }

        return string.IsNullOrWhiteSpace(mediaType) ? OctetStream : mediaType;
    }

    private static bool ShouldRewriteSegmentToTransportStream(string mediaType, string? path)
    {
        if (path?.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        if (string.IsNullOrWhiteSpace(mediaType))
            return true;

        if (mediaType.Equals(OctetStream, StringComparison.OrdinalIgnoreCase))
            return true;

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFmp4Path(string? path) =>
        path?.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) == true
        || path?.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) == true;

    private static string MediaTypeOnly(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return string.Empty;

        var semi = contentType.IndexOf(';');
        return (semi >= 0 ? contentType[..semi] : contentType).Trim();
    }
}

internal enum PlaybackResourceKind
{
    Manifest,
    MediaSegment,
    InitializationSegment,
    Other
}
