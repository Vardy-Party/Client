namespace VardyParty.Streaming;

/// <summary>
/// HLS media playlists sometimes list a 200-OK image (TikTok CDN, <c>.image</c>
/// paths) as the first "segment". HTTP health then marks the stream healthy
/// while LibVLC adaptive demux fails with "Failed to create demuxer (nil)".
/// </summary>
public static class HlsMediaSegment
{
    public static bool LooksLikeVideoSegment(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return LooksLikeVideoPath(url);

        return LooksLikeVideoPath(uri.AbsolutePath);
    }

    public static bool ContentTypeLooksLikeMedia(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return true;

        var mediaType = contentType.Split(';')[0].Trim();
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return false;
        if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool LooksLikeVideoPath(string path)
    {
        if (path.Contains(".image", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
