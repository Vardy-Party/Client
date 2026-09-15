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

    public static string FormatStreamCount(int index, int total, string? verticalResolution)
    {
        if (total <= 0)
        {
            return "Streams: 0";
        }

        return string.IsNullOrWhiteSpace(verticalResolution)
            ? $"Stream: {index}/{total}"
            : $"Stream: {index}/{total} ({verticalResolution})";
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
        string.Equals(label, "FB", StringComparison.OrdinalIgnoreCase);

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
        var buffer = bufferPercent ?? overlay?.BufferPercent;

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
            Bitrate = bitrate is > 0 ? $"{bitrate} kbps" : null,
            VideoCodec = overlay?.VideoCodec,
            AudioCodec = overlay?.AudioCodec,
            // Game title from the host; never substitute the MP chip here.
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Buffer = buffer.HasValue ? $"{buffer}%" : null,
            SourceUrl = StripQuery(sourceUrl ?? overlay?.M3u8Url ?? current?.ResolvedM3U8Url),
            RefererHost = RefererHost(refererUrl ?? overlay?.RefererUrl)
        };
    }

    public static string FormatVideoInfo(VideoInfoPanelModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Status: {model.PlaybackState ?? "unknown"}");
        if (model.StreamTotal > 0)
        {
            sb.AppendLine($"Stream: {model.StreamIndex}/{model.StreamTotal}");
        }

        if (!string.IsNullOrWhiteSpace(model.Channel))
        {
            sb.AppendLine($"Channel: {model.Channel}");
        }

        if (!string.IsNullOrWhiteSpace(model.SourceLabel))
        {
            sb.AppendLine($"Source: {model.SourceLabel}");
        }

        if (!string.IsNullOrWhiteSpace(model.Quality))
        {
            sb.AppendLine($"Quality: {model.Quality}");
        }

        var resolution = string.IsNullOrWhiteSpace(model.Resolution) ? "pending" : model.Resolution;
        sb.AppendLine(string.IsNullOrWhiteSpace(model.FrameRate)
            ? $"Resolution: {resolution}"
            : $"Resolution: {resolution} @ {model.FrameRate}");

        sb.AppendLine(string.IsNullOrWhiteSpace(model.AspectRatio)
            ? "Aspect ratio: pending"
            : $"Aspect ratio: {model.AspectRatio}");

        sb.AppendLine($"Bitrate: {model.Bitrate ?? "unknown"}");
        sb.AppendLine($"Video Codec: {model.VideoCodec ?? "unknown"}");
        sb.AppendLine($"Audio Codec: {model.AudioCodec ?? "unknown"}");

        if (!string.IsNullOrWhiteSpace(model.Title))
        {
            sb.AppendLine(model.Title);
        }

        if (!string.IsNullOrWhiteSpace(model.Buffer))
        {
            sb.AppendLine($"Buffer: {model.Buffer}");
        }

        if (!string.IsNullOrWhiteSpace(model.SourceUrl))
        {
            sb.AppendLine($"Source: {model.SourceUrl}");
        }

        if (!string.IsNullOrWhiteSpace(model.RefererHost))
        {
            sb.AppendLine($"Referer: {model.RefererHost}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Prefer MP chip / player label over catalog badge placeholders like "V2".
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
