using System.Text;
using System.Text.RegularExpressions;
using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>
/// Shared in-player chrome copy: stream-count toast and video-info panel.
/// Windows, Android (including TV), and Linux all use these strings so the
/// identity row and detail block stay aligned.
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

    /// <summary>Max characters for the stream name on the identity row (including ellipsis).</summary>
    public const int StreamNameMaxLength = 32;

    /// <summary>
    /// Compact position for the identity row, e.g. <c>2/9</c>. Empty when the pool has no streams.
    /// </summary>
    public static string FormatStreamIdentityIndex(VideoInfoPanelModel model) =>
        model.StreamTotal > 0 ? $"{model.StreamIndex}/{model.StreamTotal}" : string.Empty;

    /// <summary>Trim and ellipsize a stream/channel name so the identity row stays on one line.</summary>
    public static string? EllipsizeStreamName(string? name, int maxLength = StreamNameMaxLength)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmed = name.Trim();
        if (maxLength <= 0)
            return null;
        if (trimmed.Length <= maxLength)
            return trimmed;
        if (maxLength == 1)
            return "…";

        return trimmed[..(maxLength - 1)] + "…";
    }

    /// <summary>
    /// Vertical resolution for the identity row (e.g. <c>720p</c>). Omitted until known
    /// and while a switch is in flight so the previous picture size cannot flash.
    /// </summary>
    public static string? FormatIdentityResolution(VideoInfoPanelModel model, bool whileSwitching = false)
    {
        if (whileSwitching)
            return null;

        return ExtractVerticalResolution(model.Resolution);
    }

    /// <summary>
    /// Text after the index and V1/V2 chip on the identity row, e.g. <c>Fubo US · 720p</c>.
    /// Hosts with a visual chip render this beside the badge; Linux inlines the chip in
    /// <see cref="FormatStreamIdentityLine"/>.
    /// </summary>
    public static string FormatStreamIdentitySuffix(VideoInfoPanelModel model, bool whileSwitching = false)
    {
        var parts = new List<string>();
        var name = EllipsizeStreamName(model.Channel);
        if (!string.IsNullOrEmpty(name))
            parts.Add(name);

        var resolution = FormatIdentityResolution(model, whileSwitching);
        if (!string.IsNullOrEmpty(resolution))
            parts.Add(resolution);

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Full identity line for text-only hosts (Linux): <c>2/9 · V2 · Fubo US · 720p</c>.
    /// Omits the chip, the name, and/or the resolution when they are unknown.
    /// </summary>
    public static string FormatStreamIdentityLine(
        VideoInfoPanelModel model,
        string? sourceLabel = null,
        bool whileSwitching = false)
    {
        var index = FormatStreamIdentityIndex(model);
        if (string.IsNullOrEmpty(index))
            return string.Empty;

        var parts = new List<string> { index };
        if (!string.IsNullOrWhiteSpace(sourceLabel))
            parts.Add(sourceLabel.Trim());

        var suffix = FormatStreamIdentitySuffix(model, whileSwitching);
        if (!string.IsNullOrEmpty(suffix))
            parts.Add(suffix);

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Quality line under the identity row. When the vertical resolution is already on
    /// that row, the height prefix is stripped so <c>720p 3.4 Mbps</c> becomes <c>3.4 Mbps</c>.
    /// Empty while switching or when nothing remains.
    /// </summary>
    public static string FormatQualityLine(VideoInfoPanelModel model, bool whileSwitching = false)
    {
        if (whileSwitching || string.IsNullOrWhiteSpace(model.Quality))
            return string.Empty;

        var quality = model.Quality.Trim();
        var height = FormatIdentityResolution(model, whileSwitching: false);
        if (string.IsNullOrEmpty(height))
            return quality;

        if (quality.Equals(height, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (quality.StartsWith(height, StringComparison.OrdinalIgnoreCase))
            return quality[height.Length..].TrimStart();

        return quality;
    }

    /// <summary>
    /// Detail block under quality (Android <c>_resBrView</c>): aspect, buffer,
    /// codecs, source URL, referer. Channel and resolution live on the identity row,
    /// so they are omitted here. Empty while switching.
    /// </summary>
    public static string FormatVideoInfoDetail(VideoInfoPanelModel model, bool whileSwitching = false)
    {
        // Drop measured telemetry/URLs until the new stream settles. The identity row
        // already keeps the stream name and badge while switching.
        if (whileSwitching)
            return string.Empty;

        var lines = new List<string>();
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
    /// Keeps status, the identity row (index, source badge, stream name), and game title.
    /// </param>
    /// <remarks>
    /// Field order matches the Android/Windows multi-line panel (title, status, identity
    /// row, quality remainder, aspect/buffer/codecs/urls). Single-block hosts (Linux) use
    /// this dump, with the V1/V2 chip inlined as text.
    /// </remarks>
    public static string FormatVideoInfo(VideoInfoPanelModel model, bool whileSwitching = false)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(model.Title))
            sb.AppendLine(model.Title);

        sb.AppendLine(FormatStatusLine(model, whileSwitching));

        var identity = FormatStreamIdentityLine(model, model.SourceLabel, whileSwitching);
        if (!string.IsNullOrEmpty(identity))
            sb.AppendLine(identity);

        var quality = FormatQualityLine(model, whileSwitching);
        if (!string.IsNullOrEmpty(quality))
            sb.AppendLine(quality);

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
