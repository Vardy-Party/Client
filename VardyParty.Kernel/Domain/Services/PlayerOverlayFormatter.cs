using System.Globalization;
using System.Text.RegularExpressions;

namespace VardyParty.Kernel;

/// <summary>
/// Toolkit-free overlay string helpers shared by Android, Windows, and Linux
/// playback chrome. Hosts own widgets; this owns the math and copy.
/// </summary>
public static class PlayerOverlayFormatter
{
    private static readonly Regex VerticalResolutionRegex = new(
        @"(\d{3,4})\s*[xX]\s*(\d{3,4})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Turns <c>1920x1080</c> into <c>16:9</c>; null when unparsable.</summary>
    public static string? BuildAspect(string? resolution)
    {
        if (string.IsNullOrEmpty(resolution)) return null;
        var parts = resolution.Split('x', 'X');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return null;
        if (w <= 0 || h <= 0) return null;
        var g = Gcd(w, h);
        return $"{w / g}:{h / g}";
    }

    /// <summary>Aspect from live pixel size (Windows MediaPlayer NaturalVideo*).</summary>
    public static string? BuildAspect(uint width, uint height)
    {
        if (width == 0 || height == 0) return null;
        return BuildAspect($"{width}x{height}");
    }

    public static string StripQuery(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        try
        {
            var uri = new Uri(url);
            var builder = new UriBuilder(uri) { Query = string.Empty };
            return builder.Uri.ToString();
        }
        catch
        {
            var idx = url.IndexOf('?', StringComparison.Ordinal);
            return idx >= 0 ? url[..idx] : url;
        }
    }

    public static string RefererHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
    }

    /// <summary>Extracts <c>1080p</c> from <c>1920x1080</c> (or spaced variants).</summary>
    public static string? ExtractVerticalResolutionLabel(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution)) return null;
        var match = VerticalResolutionRegex.Match(resolution);
        return match.Success ? $"{match.Groups[2].Value}p" : null;
    }

    /// <summary>
    /// Stream toast / flash copy. Empty total → <c>Streams: 0</c>; otherwise
    /// <c>Stream: i/n</c> or <c>Stream: i/n (720p)</c>.
    /// </summary>
    public static string FormatStreamToast(int index, int total, string? verticalResolutionLabel)
    {
        if (total <= 0) return "Streams: 0";
        return string.IsNullOrWhiteSpace(verticalResolutionLabel)
            ? $"Stream: {index}/{total}"
            : $"Stream: {index}/{total} ({verticalResolutionLabel})";
    }

    public static string? MapCodecToFriendlyName(string? codec)
    {
        if (string.IsNullOrEmpty(codec)) return null;
        var lower = codec.ToLowerInvariant();
        if (lower.StartsWith("avc1", StringComparison.Ordinal) || lower.StartsWith("avc3", StringComparison.Ordinal)
            || lower.Contains("h264", StringComparison.Ordinal) || lower.Contains("avc", StringComparison.Ordinal))
            return "H.264";
        if (lower.StartsWith("hev1", StringComparison.Ordinal) || lower.StartsWith("hvc1", StringComparison.Ordinal)
            || lower.Contains("hevc", StringComparison.Ordinal) || lower.Contains("h265", StringComparison.Ordinal))
            return "H.265";
        if (lower.StartsWith("vp9", StringComparison.Ordinal) || lower.Contains("vp9", StringComparison.Ordinal))
            return "VP9";
        if (lower.StartsWith("vp8", StringComparison.Ordinal) || lower.Contains("vp8", StringComparison.Ordinal))
            return "VP8";
        if (lower.StartsWith("mp4a", StringComparison.Ordinal) || lower.Contains("aac", StringComparison.Ordinal)
            || lower.Contains("mp4a", StringComparison.Ordinal))
            return "AAC";
        if (lower.StartsWith("ac-3", StringComparison.Ordinal) || lower.Contains("ac3", StringComparison.Ordinal))
            return "AC-3";
        if (lower.StartsWith("opus", StringComparison.Ordinal) || lower.Contains("opus", StringComparison.Ordinal))
            return "Opus";
        return codec;
    }

    /// <summary>
    /// Shared overlay payload for stream switching and host chrome. Prefers
    /// health resolution/codecs; maps codec tokens via
    /// <see cref="MapCodecToFriendlyName"/>.
    /// </summary>
    public static PlayerOverlayInfo? BuildOverlayInfo(
        EnrichedStream? current,
        int index,
        int total,
        string? refererUrl = null,
        string? fallbackM3u8Url = null)
    {
        if (current is null && total <= 0)
            return null;

        var resolution = current?.Health?.Resolution ?? current?.Stream?.Resolution;
        return new PlayerOverlayInfo
        {
            Index = index,
            Total = total,
            Channel = current?.Stream?.Channel,
            BitrateKbps = current?.Stream?.BitrateKbps ?? current?.Health?.Bitrate,
            Resolution = resolution,
            FrameRate = current?.Health?.FrameRate,
            VideoCodec = MapCodecToFriendlyName(current?.Health?.VideoCodec),
            AudioCodec = MapCodecToFriendlyName(current?.Health?.AudioCodec),
            AspectRatio = BuildAspect(resolution),
            M3u8Url = current?.ResolvedM3U8Url ?? fallbackM3u8Url,
            RefererUrl = refererUrl,
            Title = current?.Stream?.Channel
        };
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}

/// <summary>ARGB tokens for catalog source badges (FB vs other).</summary>
public readonly record struct SourceBadgeStyle(byte BgA, byte BgR, byte BgG, byte BgB, byte FgA, byte FgR, byte FgG, byte FgB)
{
    public string BackgroundHex => $"#{BgR:x2}{BgG:x2}{BgB:x2}";
    public string ForegroundHex => $"#{FgR:x2}{FgG:x2}{FgB:x2}";

    public static SourceBadgeStyle? ForLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        if (string.Equals(label, "FB", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceBadgeStyle(0xFF, 0x1E, 0x3A, 0x5F, 0xFF, 0x93, 0xC5, 0xFD);
        }

        return new SourceBadgeStyle(0xFF, 0x3B, 0x07, 0x64, 0xFF, 0xD8, 0xB4, 0xFE);
    }
}
