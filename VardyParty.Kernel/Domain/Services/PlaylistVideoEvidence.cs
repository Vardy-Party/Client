namespace VardyParty.Kernel;

/// <summary>What a playlist body says about picture, before a player is attached.</summary>
public enum PlaylistVideoVerdict
{
    /// <summary>A variant or segment is video (resolution, video codec, or a video container).</summary>
    Video,

    /// <summary>Every variant or segment found is audio (0x0, audio codecs, or audio-only files).</summary>
    AudioOnly,

    /// <summary>No playlist, or not enough tags to prove either way. The player still confirms video.</summary>
    Unverified,
}

/// <summary>
/// Reads an HLS playlist for picture. Used by the startup race so a non-empty URL is not
/// a win when the playlist is audio-only (0x0 / AAC). Bytes the player would see still
/// decide after attach; this only rejects a playlist that already proves there is no picture.
/// </summary>
public static class PlaylistVideoEvidence
{
    public static PlaylistVideoVerdict Classify(string? playlist)
    {
        if (string.IsNullOrWhiteSpace(playlist)
            || !playlist.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
        {
            return PlaylistVideoVerdict.Unverified;
        }

        var sawVariant = false;
        var videoVariant = false;
        var audioVariant = false;
        var sawSegment = false;
        var videoSegment = false;
        var audioSegment = false;
        var afterStreamInf = false;

        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                sawVariant = true;
                afterStreamInf = true;
                switch (VariantKind(line))
                {
                    case PlaylistVideoVerdict.Video:
                        videoVariant = true;
                        break;
                    case PlaylistVideoVerdict.AudioOnly:
                        audioVariant = true;
                        break;
                }

                continue;
            }

            if (line.StartsWith('#'))
            {
                if (!line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                    afterStreamInf = false;
                continue;
            }

            if (afterStreamInf)
            {
                afterStreamInf = false;
                continue;
            }

            sawSegment = true;
            if (IsAudioSegment(line))
                audioSegment = true;
            else if (IsVideoSegment(line))
                videoSegment = true;
        }

        if (videoVariant || videoSegment)
            return PlaylistVideoVerdict.Video;
        if (sawVariant && audioVariant && !videoVariant)
            return PlaylistVideoVerdict.AudioOnly;
        if (sawSegment && audioSegment && !videoSegment)
            return PlaylistVideoVerdict.AudioOnly;
        return PlaylistVideoVerdict.Unverified;
    }

    private static PlaylistVideoVerdict VariantKind(string line)
    {
        var resolution = Attribute(line, "RESOLUTION");
        if (resolution is not null)
        {
            var parts = resolution.Split('x', 'X');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var width)
                && int.TryParse(parts[1], out var height))
            {
                return width > 0 && height > 0
                    ? PlaylistVideoVerdict.Video
                    : PlaylistVideoVerdict.AudioOnly;
            }
        }

        var codecs = Attribute(line, "CODECS");
        if (string.IsNullOrWhiteSpace(codecs))
            return PlaylistVideoVerdict.Unverified;

        var video = false;
        var audio = false;
        foreach (var codec in codecs.Split(','))
        {
            var token = codec.Trim().Trim('"');
            if (token.Length == 0)
                continue;
            if (IsVideoCodec(token))
                video = true;
            else if (IsAudioCodec(token))
                audio = true;
        }

        if (video)
            return PlaylistVideoVerdict.Video;
        if (audio)
            return PlaylistVideoVerdict.AudioOnly;
        return PlaylistVideoVerdict.Unverified;
    }

    private static string? Attribute(string line, string name)
    {
        var needle = name + "=";
        var index = line.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;
        var start = index + needle.Length;
        if (start < line.Length && line[start] == '"')
        {
            var end = line.IndexOf('"', start + 1);
            return end > start ? line[(start + 1)..end] : null;
        }

        var stop = line.IndexOfAny([',', ' '], start);
        return stop < 0 ? line[start..] : line[start..stop];
    }

    private static bool IsVideoCodec(string token)
        => token.StartsWith("avc1", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("hvc1", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("hev1", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("vp09", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("vp9", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("av01", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("dvh1", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudioCodec(string token)
        => token.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("ac-3", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("ec-3", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("opus", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("mp3", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudioSegment(string url)
        => EndsWithMedia(url, ".aac") || EndsWithMedia(url, ".m4a") || EndsWithMedia(url, ".cmfa");

    private static bool IsVideoSegment(string url)
        => EndsWithMedia(url, ".ts")
            || EndsWithMedia(url, ".m4s")
            || EndsWithMedia(url, ".mp4")
            || EndsWithMedia(url, ".m4v")
            || EndsWithMedia(url, ".cmfv");

    private static bool EndsWithMedia(string url, string extension)
    {
        var path = url.Split('?', '#')[0];
        return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }
}
