using VardyParty.Models;
using StreamModel = VardyParty.Models.Stream;

namespace VardyParty.Streaming;

public static class StreamHealthIdentity
{
    private const string StreamKeySeparator = "::";

    public static string? GetStreamName(StreamModel stream)
    {
        if (!stream.RequiresV2StreamSelection || stream.IsCountdown)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(stream.PlayerStream))
        {
            return stream.PlayerStream.Trim();
        }

        return string.IsNullOrWhiteSpace(stream.Channel) ? null : stream.Channel.Trim();
    }

    public static (string StreamUrl, string? StreamName) FromStream(StreamModel stream)
    {
        return (stream.Url, GetStreamName(stream));
    }

    public static string NormalizeStreamUrl(string url) => NormalizeUrl(url);

    /// <summary>
    /// Crowd health keys on the catalog/page URL. Ephemeral M3U8/DASH URLs must not be the identity.
    /// </summary>
    public static bool IsEphemeralPlaybackUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var path = NormalizeUrl(url);
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ResolveReportUrl(string? streamUrl, string? refererUrl)
    {
        if (IsEphemeralPlaybackUrl(streamUrl)
            && !string.IsNullOrWhiteSpace(refererUrl)
            && !IsEphemeralPlaybackUrl(refererUrl))
        {
            return refererUrl;
        }

        return !string.IsNullOrWhiteSpace(streamUrl) ? streamUrl : refererUrl;
    }

    public static string BuildStreamKey(string streamUrl, string? streamName = null)
    {
        var normalizedUrl = NormalizeUrl(streamUrl);
        if (string.IsNullOrWhiteSpace(streamName))
        {
            return normalizedUrl;
        }

        return $"{normalizedUrl}{StreamKeySeparator}{streamName.Trim()}";
    }

    public static bool MatchesRecommendation(StreamModel stream, string recommendedUrl, string? recommendedStreamName)
    {
        if (!string.Equals(stream.Url, recommendedUrl, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(NormalizeUrl(stream.Url), NormalizeUrl(recommendedUrl), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(recommendedStreamName))
        {
            return true;
        }

        var streamName = GetStreamName(stream);
        return !string.IsNullOrWhiteSpace(streamName)
               && string.Equals(streamName, recommendedStreamName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var noQuery = url.Split('?', 2)[0];
            return noQuery.Split('#', 2)[0].Trim();
        }

        return uri.GetLeftPart(UriPartial.Path);
    }
}
