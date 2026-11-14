using System.Text;

namespace VardyParty.Playback;

/// <summary>
/// Option 1: mid-play, retry a 5xx or non-#EXTM3U playlist on the same URL
/// (immediate, then 1s, using <see cref="PlaybackPolicy.SameStreamRetryBackoff"/>).
/// Give up after <see cref="PlaybackPolicy.MaxSameStreamRetries"/> consecutive
/// bad reloads. First attach is not retried. 401/403/442 and playlist 404/410
/// give up at once (the same URL cannot help).
/// </summary>
public sealed class RetrySameUrlPlaylistReloadStrategy : IPlaylistReloadRecoveryStrategy
{
    public string Name => PlaylistReloadStrategyNames.RetrySameUrl;

    public PlaylistReloadDecision Decide(in PlaylistReloadObservation observation)
    {
        if (PlaylistReloadEvaluation.IsPlayable(observation.StatusCode, observation.Body.Span))
            return PlaylistReloadDecision.Serve();

        var kind = PlaylistReloadEvaluation.ClassifyFailure(observation.StatusCode, observation.Body.Span);

        if (observation.IsFirstAttach)
            return PlaylistReloadDecision.GiveUp(kind, "first-attach playlist is not playable");

        if (kind == PlaybackErrorKind.ConfirmedDead
            || PlaylistReloadEvaluation.IsAuthRejection(observation.StatusCode))
        {
            return PlaylistReloadDecision.GiveUp(kind, "same-URL retry cannot recover this playlist failure");
        }

        if (observation.Attempt < PlaybackPolicy.MaxSameStreamRetries)
        {
            var delay = PlaybackPolicy.SameStreamRetryBackoff(observation.Attempt);
            return PlaylistReloadDecision.Retry(delay, $"retry same URL ({kind})");
        }

        return PlaylistReloadDecision.GiveUp(kind, "playlist reload budget exhausted");
    }
}

public static class PlaylistReloadStrategies
{
    public static IPlaylistReloadRecoveryStrategy Default { get; } = new RetrySameUrlPlaylistReloadStrategy();

    public static IPlaylistReloadRecoveryStrategy Resolve(string? name)
    {
        // Fetch-layer Decide() is always option 1. Option 2 (switch-back) is
        // selected alongside it via <see cref="IsSwitchBackEnabled"/>.
        return Default;
    }

    /// <summary>
    /// Option 2 is on for the default combination and for any unknown name
    /// (a typo must not disable auto-return). Set
    /// <see cref="PlaylistReloadStrategyNames.RetrySameUrl"/> to use option 1 only.
    /// </summary>
    public static bool IsSwitchBackEnabled(string? name) =>
        string.IsNullOrWhiteSpace(name)
        || !name.Equals(PlaylistReloadStrategyNames.RetrySameUrl, StringComparison.OrdinalIgnoreCase);
}

public static class PlaylistReloadEvaluation
{
    public static bool IsPlayable(int statusCode, ReadOnlySpan<byte> body) =>
        statusCode is 200 or 206 && StartsWithExtm3u(body);

    public static bool StartsWithExtm3u(ReadOnlySpan<byte> body)
    {
        var text = Encoding.UTF8.GetString(body);
        return text.AsSpan().TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal);
    }

    public static bool IsPlaylistUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        var path = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            path = uri.AbsolutePath;
        return path.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAuthRejection(int statusCode) => statusCode is 401 or 403 or 442;

    public static PlaybackErrorKind ClassifyFailure(int statusCode, ReadOnlySpan<byte> body)
    {
        if (statusCode is 404 or 410)
            return PlaybackErrorKind.ConfirmedDead;
        if (IsAuthRejection(statusCode))
            return PlaybackErrorKind.TokenOrPlaylistInvalid;
        if (statusCode is 500 or 502 or 503 or 504 or 0)
            return PlaybackErrorKind.CdnUnreachable;
        // 200/206 with a non-HLS body (HTML, empty, JSON), or any other surprise.
        return PlaybackErrorKind.TokenOrPlaylistInvalid;
    }

    public static string GiveUpMessage(PlaybackErrorKind kind, int statusCode, string reason)
    {
        // Keep "HTTP {code}" in the engine message so ClassifyPlaybackError can
        // still map 5xx to CdnUnreachable and 401/403/442 to token reject.
        // A 200 non-HLS body must not be prefixed "HTTP 200" — that would classify
        // as Transient and skip the Hadcrack immediate-advance path.
        if (kind == PlaybackErrorKind.CdnUnreachable)
        {
            return statusCode is >= 100 and <= 599
                ? $"HTTP {statusCode} {reason}"
                : $"network connection failed ({reason})";
        }

        if (statusCode is 401 or 403 or 442 or 404 or 410)
            return $"HTTP {statusCode} {reason}";

        if (kind == PlaybackErrorKind.TokenOrPlaylistInvalid)
        {
            return $"Source error [parsing manifest malformed] Input does not start with the #EXTM3U header ({reason})";
        }

        return reason;
    }
}
