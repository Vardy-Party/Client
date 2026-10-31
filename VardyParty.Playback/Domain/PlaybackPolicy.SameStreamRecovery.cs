namespace VardyParty.Playback;

/// <summary>
/// How a playback error should be handled. The rule: retry the stream that is
/// playing before switching away from it. Only a confirmed-dead stream switches
/// at once.
/// </summary>
public enum PlaybackErrorKind
{
    /// <summary>
    /// A buffer stall, a segment timeout or 404, a stale playlist, a health decline.
    /// First reload the same URL, then get a fresh <c>/play</c> for the same stream.
    /// </summary>
    Transient,

    /// <summary>
    /// The token was rejected or has expired: 401/403, or a playlist body that is
    /// not HLS (the CDN error page "Input does not start with the #EXTM3U header").
    /// Reloading the same URL cannot help, so go straight to a fresh <c>/play</c>.
    /// </summary>
    TokenOrPlaylistInvalid,

    /// <summary>
    /// The stream is gone: playlist 404/410, no m3u8, decoy, the CDN host cannot
    /// be resolved or refuses connections, or the format is unsupported. Switch now.
    /// </summary>
    ConfirmedDead
}

/// <summary>The next step of a same-stream retry.</summary>
public enum SameStreamRetryStep
{
    /// <summary>Re-attach the current URL. The engine reloads the playlist.</summary>
    ReloadPlaylist,

    /// <summary>Resolve a fresh M3U8 for the same pool row and attach it.</summary>
    FreshResolve
}

public static partial class PlaybackPolicy
{
    /// <summary>
    /// How many same-stream retries an established stream gets before the session
    /// switches away. Attempt 1 reloads (for transient errors) and attempts 2-3
    /// fetch a fresh <c>/play</c>.
    /// </summary>
    public const int MaxSameStreamRetries = 3;

    /// <summary>A health decline gets one reload before the stream is declined.</summary>
    public const int MaxHealthDeclineRetries = 1;

    /// <summary>
    /// The retry budget refills after this long without an error. Tokens that
    /// expire every couple of minutes then get a fresh <c>/play</c> each time,
    /// instead of the stream being dropped after the third expiry.
    /// </summary>
    public static readonly TimeSpan SameStreamRetryWindow = TimeSpan.FromSeconds(90);

    private static readonly string[] ConfirmedDeadMarkers =
    [
        "no m3u8",
        "decoy",
        "unable to resolve host",
        "unknownhostexception",
        "name not resolved",
        "name or service not known",
        "no such host",
        "connection refused",
        "econnrefused",
        "failed to connect",
        "unable to connect",
        "connectexception",
        "no route to host",
        "network is unreachable",
        "not supported",
        "unsupported",
        "decoder init",
        "mediacodecvideodecoderexception"
    ];

    private static readonly string[] TokenMarkers =
    [
        "401",
        "403",
        "forbidden",
        "unauthorized",
        "#extm3u",
        "playlist parse",
        "manifest malformed",
        "parsing_manifest_malformed",
        "parsing manifest malformed",
        "invalid http content type"
    ];

    private static readonly string[] PlaylistContextMarkers =
    [
        "m3u8",
        "playlist",
        "manifest"
    ];

    /// <summary>
    /// Classifies an engine error message. Hosts must put the cause chain in
    /// the message (Android "Source error" alone carries nothing). An unknown
    /// message counts as <see cref="PlaybackErrorKind.Transient"/>, so the
    /// stream is retried and not dropped.
    /// </summary>
    public static PlaybackErrorKind ClassifyPlaybackError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return PlaybackErrorKind.Transient;

        var text = message.ToLowerInvariant();

        // A playlist that is 404/410 is gone. A segment 404 is often a transient
        // CDN edge miss, so it stays Transient and the fresh /play decides.
        if ((ContainsStatus(text, "404") || ContainsStatus(text, "410"))
            && PlaylistContextMarkers.Any(text.Contains))
        {
            return PlaybackErrorKind.ConfirmedDead;
        }

        foreach (var marker in ConfirmedDeadMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
                return PlaybackErrorKind.ConfirmedDead;
        }

        foreach (var marker in TokenMarkers)
        {
            if (marker.All(char.IsDigit) ? ContainsStatus(text, marker) : text.Contains(marker, StringComparison.Ordinal))
                return PlaybackErrorKind.TokenOrPlaylistInvalid;
        }

        return PlaybackErrorKind.Transient;
    }

    /// <summary>
    /// Whether an established stream gets another same-stream attempt.
    /// <paramref name="retriesUsed"/> is how many have run in the current window.
    /// </summary>
    public static bool ShouldRetrySameStream(PlaybackErrorKind kind, int retriesUsed)
        => kind != PlaybackErrorKind.ConfirmedDead && retriesUsed < MaxSameStreamRetries;

    /// <summary>
    /// Attempt 1 of a transient error reloads the same URL. Any later attempt,
    /// or any token or playlist error, fetches a fresh <c>/play</c>.
    /// </summary>
    public static SameStreamRetryStep NextSameStreamRetryStep(PlaybackErrorKind kind, int attempt)
        => kind == PlaybackErrorKind.Transient && attempt <= 1
            ? SameStreamRetryStep.ReloadPlaylist
            : SameStreamRetryStep.FreshResolve;

    /// <summary>
    /// The wait before a same-stream fresh <c>/play</c>: 0s for attempt 1, then
    /// 1s, then 3s. This keeps a flapping CDN from being hammered.
    /// </summary>
    public static TimeSpan SameStreamRetryBackoff(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.Zero,
        2 => TimeSpan.FromSeconds(1),
        _ => TimeSpan.FromSeconds(3)
    };

    /// <summary>
    /// Whether the retry budget refills, because the last retry was long enough ago.
    /// </summary>
    public static bool ShouldResetSameStreamRetries(DateTimeOffset? lastRetryAt, DateTimeOffset now)
        => lastRetryAt is null || now - lastRetryAt.Value >= SameStreamRetryWindow;

    /// <summary>
    /// A stream that has not started gets one fresh <c>/play</c> before it is
    /// dropped. A failed cached URL always gets one (the original rule). A fresh
    /// URL gets one too, unless the error already proves the stream is dead.
    /// </summary>
    public static bool ShouldRetryFreshOnStartError(PlaybackSessionSnapshot snapshot, PlaybackErrorKind kind)
        => ShouldRetryFreshResolve(snapshot)
           || (!snapshot.CacheRetryUsed
               && !snapshot.HasEstablishedPlayback
               && kind != PlaybackErrorKind.ConfirmedDead);

    /// <summary>
    /// Builds the message an engine Error carries: the message, the error-code
    /// name, then the cause chain. Android's "Source error" alone cannot be
    /// classified. The cause ("ParserException: Input does not start with the
    /// #EXTM3U header", an expired token) is what makes the retry work.
    /// </summary>
    public static string BuildEngineErrorMessage(string? message, int? errorCode, string? causeSummary)
    {
        var parts = new List<string>(3) { string.IsNullOrWhiteSpace(message) ? "Playback error" : message.Trim() };
        var codeName = DescribeExoErrorCode(errorCode);
        if (codeName != null)
            parts.Add($"[{codeName}]");
        if (!string.IsNullOrWhiteSpace(causeSummary))
            parts.Add(causeSummary.Trim());
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Media3 <c>PlaybackException</c> error-code names (the stable public
    /// constants). Unknown codes return null.
    /// </summary>
    public static string? DescribeExoErrorCode(int? errorCode) => errorCode switch
    {
        1002 => "behind live window",
        1003 => "timeout",
        2000 => "io unspecified",
        2001 => "network connection failed",
        2002 => "network connection timeout",
        2003 => "invalid http content type",
        2004 => "bad http status",
        2005 => "file not found",
        3001 => "parsing container malformed",
        3002 => "parsing manifest malformed",
        3003 => "parsing container unsupported",
        3004 => "parsing manifest unsupported",
        4001 => "decoder init failed",
        4002 => "decoder query failed",
        4003 => "decoding failed",
        4004 => "decoding format exceeds capabilities",
        4005 => "decoding format unsupported",
        _ => null
    };

    private static bool ContainsStatus(string text, string code)
    {
        var index = text.IndexOf(code, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsDigit(text[index - 1]);
            var afterIndex = index + code.Length;
            var after = afterIndex >= text.Length || !char.IsDigit(text[afterIndex]);
            if (before && after)
                return true;
            index = text.IndexOf(code, index + 1, StringComparison.Ordinal);
        }

        return false;
    }
}