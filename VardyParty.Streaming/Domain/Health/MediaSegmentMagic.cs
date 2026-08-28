using System;

namespace VardyParty.Streaming;

/// <summary>
/// Cheap prefix sniff so health checks do not mark JPEG/PNG/HTML "segments"
/// Healthy. Field: Sportsbest HLS pointed at TikTok CDN <c>.image</c> URLs that
/// HEAD 200 but LibVLC adaptive demux fails with "Failed to create demuxer".
/// </summary>
public static class MediaSegmentMagic
{
    public const int ProbeByteCount = 512;

    public static bool LooksLikePlayableMedia(ReadOnlySpan<byte> prefix, string? contentType = null)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var ct = contentType.Trim();
            if (ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                ct.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                ct.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                ct.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (prefix.IsEmpty)
        {
            return false;
        }

        // MPEG-TS sync byte (often every 188 bytes; first byte is enough for a probe).
        if (prefix[0] == 0x47)
        {
            return true;
        }

        // ID3 tag then AAC/TS — common for audio-bearing HLS.
        if (prefix.Length >= 3 && prefix[0] == (byte)'I' && prefix[1] == (byte)'D' && prefix[2] == (byte)'3')
        {
            return true;
        }

        // ADTS AAC
        if (prefix.Length >= 2 && prefix[0] == 0xFF && (prefix[1] & 0xF0) == 0xF0)
        {
            return true;
        }

        // ISO BMFF / fMP4 (ftyp / styp / moof / mdat boxes)
        if (ContainsIsoBox(prefix, "ftyp") ||
            ContainsIsoBox(prefix, "styp") ||
            ContainsIsoBox(prefix, "moof") ||
            ContainsIsoBox(prefix, "mdat") ||
            ContainsIsoBox(prefix, "sidx"))
        {
            return true;
        }

        // WebVTT / plain playlists accidentally used as "segments" — not AV.
        if (StartsWithAscii(prefix, "WEBVTT") || StartsWithAscii(prefix, "#EXTM3U"))
        {
            return false;
        }

        // Obvious non-media
        if (IsJpeg(prefix) || IsPng(prefix) || IsGif(prefix) || IsWebp(prefix) || IsHtmlOrXml(prefix))
        {
            return false;
        }

        // Unknown binary with a video-ish Content-Type — give the player a chance.
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var ct = contentType;
            if (ct.Contains("video/", StringComparison.OrdinalIgnoreCase) ||
                ct.Contains("audio/", StringComparison.OrdinalIgnoreCase) ||
                ct.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
                ct.Contains("mp2t", StringComparison.OrdinalIgnoreCase) ||
                ct.Contains("mp4", StringComparison.OrdinalIgnoreCase) ||
                ct.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsIsoBox(ReadOnlySpan<byte> data, string fourCc)
    {
        if (data.Length < 8 || fourCc.Length != 4)
        {
            return false;
        }

        var a = (byte)fourCc[0];
        var b = (byte)fourCc[1];
        var c = (byte)fourCc[2];
        var d = (byte)fourCc[3];
        // Box type sits at offset 4 within each box; scan a few candidate starts.
        for (var i = 0; i + 8 <= data.Length && i <= 64; i++)
        {
            if (data[i + 4] == a && data[i + 5] == b && data[i + 6] == c && data[i + 7] == d)
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> data, string ascii)
    {
        if (data.Length < ascii.Length)
        {
            return false;
        }

        for (var i = 0; i < ascii.Length; i++)
        {
            if (data[i] != (byte)ascii[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsJpeg(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    private static bool IsPng(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    private static bool IsGif(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F';

    private static bool IsWebp(ReadOnlySpan<byte> data) =>
        data.Length >= 12 &&
        data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
        data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P';

    private static bool IsHtmlOrXml(ReadOnlySpan<byte> data)
    {
        var trim = data;
        while (!trim.IsEmpty && (trim[0] == (byte)' ' || trim[0] == (byte)'\n' || trim[0] == (byte)'\r' || trim[0] == (byte)'\t'))
        {
            trim = trim[1..];
        }

        if (trim.IsEmpty)
        {
            return false;
        }

        if (trim[0] == (byte)'<')
        {
            return true;
        }

        // UTF-8 BOM + '<'
        return trim.Length >= 4 &&
               trim[0] == 0xEF && trim[1] == 0xBB && trim[2] == 0xBF && trim[3] == (byte)'<';
    }
}
