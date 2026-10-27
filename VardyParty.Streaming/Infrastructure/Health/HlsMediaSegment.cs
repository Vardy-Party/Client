namespace VardyParty.Streaming;

/// <summary>
/// HLS media playlists sometimes list a 200-OK image (TikTok CDN, <c>.image</c>
/// / <c>~tplv-</c> paths) as the first "segment". HTTP health then marks the
/// stream healthy while Exo/LibVLC fail (PNG body or demuxer nil).
/// URL shape is only a fast pre-filter: image-looking segments are then classified
/// by bytes (<see cref="SegmentPayload"/>) because PNG/WebP-wrapped MPEG-TS on the same
/// CDN is playable once unwrapped (<see cref="PngWrappedTs"/>, <see cref="WebpWrappedTs"/>).
/// </summary>
public static class HlsMediaSegment
{
    private static readonly string[] VideoPathExtensions =
    [
        ".ts", ".m4s", ".mp4", ".m4a", ".aac", ".cmfv", ".cmfa"
    ];

    public static bool LooksLikeVideoSegment(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return LooksLikeVideoPath(url);

        if (!LooksLikeVideoPath(uri.AbsolutePath))
            return false;

        // TikTok CDN decoys often omit .image but still serve image/* for
        // extension-less ~tplv- object paths. Require a known media extension.
        if (uri.Host.Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase)
            && !HasVideoPathExtension(uri.AbsolutePath))
        {
            return false;
        }

        return true;
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

        // TikTok image transform token embedded in the path.
        if (path.Contains("~tplv-", StringComparison.OrdinalIgnoreCase))
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

    private static bool HasVideoPathExtension(string path)
    {
        foreach (var ext in VideoPathExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
