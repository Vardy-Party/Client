using System.Text;
using System.Text.RegularExpressions;
using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>
/// Shared in-player chrome copy: stream-count toast and video-info panel.
/// Windows and Android build the same strings; Linux uses this helper so
/// the wording stays aligned without a third hand-rolled formatter.
/// </summary>
public static class PlayerChromeText
{
    private static readonly Regex ResolutionPair = new(
        @"(\d{3,4})\s*[xX]\s*(\d{3,4})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex VerticalOnly = new(
        @"^\d{3,4}p$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Switch toast copy — index/total only. Resolution/bitrate stay off this flash
    /// (see <see cref="PlayerOverlayFormatter.FormatStreamToast"/>).
    /// </summary>
    public static string FormatStreamCount(int index, int total, string? verticalResolution)
    {
        _ = verticalResolution;
        if (total <= 0)
        {
            return "Streams: 0";
        }

        return $"Stream: {index}/{total}";
    }

    public static string FormatStreamHint(int index, int total) =>
        total > 0 ? $"{index}/{total}" : string.Empty;

    public static string? ExtractVerticalResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return null;
        }

        var match = ResolutionPair.Match(resolution);
        if (match.Success)
        {
            return $"{match.Groups[2].Value}p";
        }

        return VerticalOnly.IsMatch(resolution.Trim()) ? resolution.Trim() : null;
    }

    public static bool IsFacebookSource(string? label) =>
        string.Equals(label, "V1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(label, "FB", StringComparison.OrdinalIgnoreCase);

    public static string StripQuery(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        try
        {
            var uri = new Uri(url);
            var builder = new UriBuilder(uri) { Query = string.Empty };
            return builder.Uri.ToString();
        }
        catch (UriFormatException)
        {
            var idx = url.IndexOf('?', StringComparison.Ordinal);
            return idx >= 0 ? url[..idx] : url;
        }
    }

    public static string? BuildAspectRatio(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return null;
        }

        var match = ResolutionPair.Match(resolution);
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups[1].Value, out var width) ||
            !int.TryParse(match.Groups[2].Value, out var height) ||
            width <= 0 ||
            height <= 0)
        {
            return null;
        }

        var divisor = GreatestCommonDivisor(width, height);
        return $"{width / divisor}:{height / divisor}";
    }

    private static int GreatestCommonDivisor(int a, int b) =>
        b == 0 ? a : GreatestCommonDivisor(b, a % b);

    /// <summary>
    /// Quality line from what playback actually measured. A media playlist often has
    /// no <c>#EXT-X-STREAM-INF</c>, so the health record is "Unknown Quality" while
    /// the player already has a picture size. Measured size and bitrate win.
    /// The playlist label is used only when nothing was measured.
    /// </summary>
    public static string FormatMeasuredQuality(string? measuredResolution, int? bitrateKbps, string? playlistQuality)
    {
        var parts = new List<string>();
        var height = ExtractVerticalResolution(measuredResolution);
        if (!string.IsNullOrEmpty(height))
            parts.Add(height);
        if (bitrateKbps is > 0)
            parts.Add(FormatBitrateMbps(bitrateKbps.Value));
        if (parts.Count > 0)
            return string.Join(' ', parts);

        if (!string.IsNullOrWhiteSpace(playlistQuality)
            && !playlistQuality.Equals("Unknown Quality", StringComparison.OrdinalIgnoreCase))
        {
            return playlistQuality.Trim();
        }

        return "Unknown Quality";
    }

    /// <summary>One decimal Mbps, including rates under 1 Mbps (500 kbps is 0.5 Mbps).</summary>
    public static string FormatBitrateMbps(int kbps)
    {
        var tenths = (kbps + 50) / 100;
        return $"{tenths / 10}.{tenths % 10} Mbps";
    }

    public static string RefererHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
    }

    public static VideoInfoPanelModel FromPlayback(
        PlayerOverlayInfo? overlay,
        EnrichedStream? current,
        string? playbackState,
        string? title,
        string? sourceUrl,
        string? refererUrl,
        int? bufferPercent = null)
    {
        string? quality = null;
        try
        {
            quality = current?.GetQualityDisplay();
        }
        catch (Exception)
        {
            quality = null;
        }

        var resolution = overlay?.Resolution
            ?? current?.Health?.Resolution
            ?? current?.Stream?.Resolution;
        double? frameRate = overlay?.FrameRate;
        if (frameRate is null && current?.Health?.FrameRate is { } fps)
        {
            frameRate = fps;
        }

        var bitrate = overlay?.BitrateKbps ?? current?.Stream?.BitrateKbps ?? current?.Health?.Bitrate;
        quality = FormatMeasuredQuality(resolution, bitrate, quality);
        var buffer = bufferPercent ?? overlay?.BufferPercent;
        var videoCodec = PlayerOverlayFormatter.MapCodecToFriendlyName(overlay?.VideoCodec)
            ?? PlayerOverlayFormatter.MapCodecToFriendlyName(current?.Health?.VideoCodec);
        var audioCodec = PlayerOverlayFormatter.MapCodecToFriendlyName(overlay?.AudioCodec)
            ?? PlayerOverlayFormatter.MapCodecToFriendlyName(current?.Health?.AudioCodec);

        return new VideoInfoPanelModel
        {
            PlaybackState = string.IsNullOrWhiteSpace(playbackState) ? "unknown" : playbackState,
            StreamIndex = overlay?.Index ?? 0,
            StreamTotal = overlay?.Total ?? 0,
            Channel = PreferChipLabel(overlay?.Channel, current?.Stream),
            SourceLabel = current?.Stream?.CatalogSourceBadgeLabel,
            Quality = quality,
            Resolution = resolution,
            FrameRate = frameRate.HasValue ? $"{frameRate:0.##} fps" : null,
            AspectRatio = overlay?.AspectRatio ?? BuildAspectRatio(resolution),
            Bitrate = bitrate is > 0 ? FormatBitrateMbps(bitrate.Value) : null,
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            // Game title from the host; never substitute the MP chip here.
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Buffer = buffer.HasValue ? $"Buffer {buffer}%" : null,
            SourceUrl = StripQuery(sourceUrl ?? overlay?.M3u8Url ?? current?.ResolvedM3U8Url),
            RefererHost = RefererHost(refererUrl ?? overlay?.RefererUrl)
        };
    }

    /// <summary>Status line shared by Android / Windows / Linux video-info panels.</summary>
    public static string FormatStatusLine(VideoInfoPanelModel model, bool whileSwitching = false) =>
        whileSwitching
            ? "Status: Switching"
            : $"Status: {(string.IsNullOrWhiteSpace(model.PlaybackState) ? "unknown" : model.PlaybackState)}";

    /// <summary>Stream index line, or empty when the pool has no healthy streams.</summary>
    public static string FormatStreamIndexLine(VideoInfoPanelModel model) =>
        model.StreamTotal > 0 ? $"Stream: {model.StreamIndex}/{model.StreamTotal}" : string.Empty;

    /// <summary>
    /// Detail block under quality (Android <c>_resBrView</c>): channel, aspect, buffer,
    /// codecs, source URL, referer. Empty while switching.
    /// </summary>
    public static string FormatVideoInfoDetail(VideoInfoPanelModel model, bool whileSwitching = false)
    {
        // Keep channel while switching; drop measured telemetry/URLs until the new stream settles.
        if (whileSwitching)
        {
            return !string.IsNullOrWhiteSpace(model.Channel)
                ? $"Channel: {model.Channel}"
                : string.Empty;
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(model.Channel))
            lines.Add($"Channel: {model.Channel}");
        if (!string.IsNullOrWhiteSpace(model.AspectRatio))
            lines.Add($"Aspect ratio: {model.AspectRatio}");
        // Always keep the Buffer row when known (including "Buffer 0%") so the panel
        // does not reflow when fill drops to empty.
        if (!string.IsNullOrWhiteSpace(model.Buffer))
            lines.Add(model.Buffer);

        var codecParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(model.VideoCodec))
            codecParts.Add($"Video: {model.VideoCodec}");
        if (!string.IsNullOrWhiteSpace(model.AudioCodec))
            codecParts.Add($"Audio: {model.AudioCodec}");
        if (codecParts.Count > 0)
            lines.Add(string.Join(" / ", codecParts));

        if (!string.IsNullOrWhiteSpace(model.SourceUrl))
            lines.Add($"Source: {model.SourceUrl}");
        if (!string.IsNullOrWhiteSpace(model.RefererHost))
            lines.Add($"Referer: {model.RefererHost}");

        return string.Join('\n', lines);
    }

    /// <param name="whileSwitching">
    /// When true (Next/Prev/chip in flight), omit measured stream telemetry and URLs so
    /// the video-info overlay does not flash the previous stream's size/bitrate/m3u8.
    /// Keeps status, index, channel, source badge, and game title.
    /// </param>
    /// <remarks>
    /// Field order matches the Android multi-line panel (title, status, stream, channel via
    /// detail, quality, aspect/buffer/codecs/urls). Single-block hosts (Linux) use this dump.
    /// </remarks>
    public static string FormatVideoInfo(VideoInfoPanelModel model, bool whileSwitching = false)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(model.Title))
            sb.AppendLine(model.Title);

        sb.AppendLine(FormatStatusLine(model, whileSwitching));

        var indexLine = FormatStreamIndexLine(model);
        if (!string.IsNullOrEmpty(indexLine))
            sb.AppendLine(indexLine);

        if (!string.IsNullOrWhiteSpace(model.SourceLabel))
            sb.AppendLine($"Source: {model.SourceLabel}");

        if (!whileSwitching && !string.IsNullOrWhiteSpace(model.Quality))
            sb.AppendLine(model.Quality);

        var detail = FormatVideoInfoDetail(model, whileSwitching);
        if (!string.IsNullOrEmpty(detail))
            sb.Append(detail);

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Prefer MP chip / player label over catalog badge placeholders like "V1"/"V2".
    /// </summary>
    private static string? PreferChipLabel(string? overlayChannel, Kernel.Stream? stream)
    {
        var fromStream = PlayerOverlayFormatter.ResolveChipOrChannelLabel(stream);
        if (!string.IsNullOrWhiteSpace(fromStream))
            return fromStream;

        if (string.IsNullOrWhiteSpace(overlayChannel))
            return null;

        var trimmed = overlayChannel.Trim();
        if (string.Equals(trimmed, "V2", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "V1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "MP", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "FB", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }
}

public sealed class VideoInfoPanelModel
{
    public string? PlaybackState { get; init; }
    public int StreamIndex { get; init; }
    public int StreamTotal { get; init; }
    public string? Channel { get; init; }
    public string? SourceLabel { get; init; }
    public string? Quality { get; init; }
    public string? Resolution { get; init; }
    public string? FrameRate { get; init; }
    public string? AspectRatio { get; init; }
    public string? Bitrate { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? Title { get; init; }
    public string? Buffer { get; init; }
    public string? SourceUrl { get; init; }
    public string? RefererHost { get; init; }
}
