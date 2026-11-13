namespace VardyParty.Playback;

/// <summary>
/// Refused downloads (HTTP 401/403) and the error text the user sees
/// (Windows log 13 Nov 2026 19:51-19:55; Jon's screenshot of "Unsupported video type or
/// invalid file path" on a V2 chip whose every segment was 403).
/// </summary>
public static partial class PlaybackPolicy
{
    /// <summary>
    /// Refused segment downloads in a row that end the attempt. A CDN that refused two
    /// segments in a row will refuse the rest: waiting for five (or for Media Foundation's
    /// own MediaFailed, ~5s on 13 Nov) only lengthens the black screen.
    /// </summary>
    public const int MaxConsecutiveRefusedDownloads = 2;

    /// <summary>HTTP status that means the server refused the request (token / Referer / auth).</summary>
    public static bool IsRefusedStatus(int statusCode) => statusCode is 401 or 403;

    /// <summary>Hard failure: <see cref="MaxConsecutiveDownloadFailures"/> of any kind, or refused ones in a row.</summary>
    public static bool IsHardDownloadFailure(int consecutiveFailures, int consecutiveRefused)
        => IsHardDownloadFailure(consecutiveFailures) || consecutiveRefused >= MaxConsecutiveRefusedDownloads;

    /// <summary>
    /// Error reason for refused downloads. Classified as
    /// <see cref="PlaybackErrorKind.TokenOrPlaylistInvalid"/> (one fresh resolve, then the
    /// next stream), never as format death, whatever the OS adds.
    /// </summary>
    public static string RefusedDownloadReason(int statusCode) => $"Stream refused ({statusCode})";

    /// <summary>
    /// HTTP status in a failed download's error text, else 0: "HTTP 403" (Android managed
    /// data source), "Response code: 403" (Media3 InvalidResponseCodeException), "status=403"
    /// (Windows segment log). Lets Android feed refused segments into
    /// <see cref="IsHardDownloadFailure(int, int)"/> like Windows.
    /// </summary>
    public static int HttpStatusFromErrorText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        var match = HttpStatusPattern().Match(text);
        return match.Success
               && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var code)
               && code is >= 100 and <= 599
            ? code
            : 0;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:\bHTTP(?:/\d(?:\.\d)?)?|\bResponse code:?|\bstatus\s*[=:]?)\s*(\d{3})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex HttpStatusPattern();

    /// <summary>
    /// A first frame / video size belongs to the current attach only once the player has
    /// been handed the session's URL. Between BeginAttach and the engine attach the old
    /// stream's frame must not confirm the new one (an audio-only target would then pass
    /// as "video shown").
    /// </summary>
    public static bool IsPictureForCurrentAttach(string? playerUrl, string? sessionCurrentUrl)
        => !string.IsNullOrWhiteSpace(playerUrl)
           && !string.IsNullOrWhiteSpace(sessionCurrentUrl)
           && string.Equals(playerUrl, sessionCurrentUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Value for <c>AdaptiveMediaSourceDownloadResult.ExtendedStatus</c> on a failed download.
    /// WinRT rejects a bare HTTP status (403 threw "The parameter is incorrect" on every
    /// failed segment); it takes a failure HRESULT, so HTTP statuses map to
    /// HTTP_E_STATUS_* (0x80190000 + status, e.g. 0x80190193 for 403), anything else E_FAIL.
    /// </summary>
    public static uint WindowsDownloadFailureStatus(int statusCode)
        => statusCode is >= 100 and <= 599 ? 0x80190000u | (uint)statusCode : 0x80004005u;

    /// <summary>
    /// Short, accurate text for a stream failure; <paramref name="movingOn"/> adds
    /// " - trying the next stream". Raw engine text ("Unsupported video type or invalid file
    /// path", HRESULTs) is never shown.
    /// </summary>
    public static string UserFacingStreamError(string? reason, bool movingOn)
    {
        var text = reason ?? string.Empty;
        string head;
        if (text.Contains("audio only", StringComparison.OrdinalIgnoreCase))
            head = "No picture (audio only)";
        else if (text.Contains("declined", StringComparison.OrdinalIgnoreCase))
            head = text.Contains("buffering", StringComparison.OrdinalIgnoreCase)
                ? "Stream kept buffering"
                : "Stream too slow";
        else if (RefusedStatusIn(text) is { } refused)
            head = $"Stream refused ({refused})";
        else
        {
            head = ClassifyPlaybackError(string.IsNullOrWhiteSpace(text) ? null : text) switch
            {
                PlaybackErrorKind.TokenOrPlaylistInvalid => "Stream link expired",
                PlaybackErrorKind.CdnUnreachable => "Stream server unreachable",
                PlaybackErrorKind.ConfirmedDead => text.Contains("support", StringComparison.OrdinalIgnoreCase)
                    ? "Stream format not playable"
                    : "Stream is offline",
                _ => "Stream failed",
            };
        }

        return movingOn ? $"{head} - trying the next stream" : head;
    }

    private static int? RefusedStatusIn(string text)
    {
        var lower = text.ToLowerInvariant();
        if (ContainsStatus(lower, "403") || lower.Contains("forbidden", StringComparison.Ordinal))
            return 403;
        if (ContainsStatus(lower, "401") || lower.Contains("unauthorized", StringComparison.Ordinal))
            return 401;
        return null;
    }
}