using System.Text;
using System.Text.RegularExpressions;

namespace VardyParty.Playback;

/// <summary>
/// Runs <see cref="IPlaylistReloadRecoveryStrategy"/> over successive fetches of
/// the same playlist URL. Platforms supply the fetch; they must not give a
/// <see cref="PlaylistReloadOutcome.Kind"/> of <see cref="PlaylistReloadOutcomeKind.Failed"/>
/// body to the player.
/// </summary>
public static class PlaylistReloadCoordinator
{
    /// <summary>
    /// Windows <c>DownloadRequested</c> deferral budget. Holding longer risks AMS
    /// abandoning the request. If the next retry delay would exceed the remaining
    /// budget, the coordinator serves <see cref="PlaylistReloadRequest.LastGoodPlaylist"/>
    /// when it has one (documented escape; prefer holding otherwise).
    /// </summary>
    public static readonly TimeSpan WindowsDeferralBudget = PlaybackPolicy.WindowsPlaylistDeferralBudget;

    public static async Task<PlaylistReloadOutcome> RunAsync(
        IPlaylistReloadRecoveryStrategy strategy,
        PlaylistReloadRequest request,
        Func<CancellationToken, Task<PlaylistReloadFetchResult>> fetch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(fetch);

        var delay = request.Delay ?? ((TimeSpan d, CancellationToken ct) => Task.Delay(d, ct));
        var clock = request.Clock ?? (() => DateTimeOffset.UtcNow);
        var started = clock();
        var attempt = 0;
        PlaylistReloadFetchResult? last = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            last = await fetch(cancellationToken).ConfigureAwait(false);
            var observation = new PlaylistReloadObservation(
                attempt,
                request.IsFirstAttach,
                last.StatusCode,
                last.Body,
                last.ContentType);
            var decision = strategy.Decide(observation);
            Log(request, observation, decision, outcome: Describe(decision));

            if (decision.Action == PlaylistReloadAction.Serve)
                return PlaylistReloadOutcome.Serve(last.Body, last.ContentType, last.StatusCode);

            if (decision.Action == PlaylistReloadAction.GiveUp)
            {
                var kind = decision.ErrorKind ?? PlaylistReloadEvaluation.ClassifyFailure(last.StatusCode, last.Body);
                return PlaylistReloadOutcome.Failed(
                    kind,
                    PlaylistReloadEvaluation.GiveUpMessage(kind, last.StatusCode, decision.Reason),
                    last.StatusCode);
            }

            // Retry: maybe the Windows deferral budget cannot hold the delay.
            if (request.MaxHold is { } budget)
            {
                var remaining = budget - (clock() - started);
                if (remaining < decision.Delay)
                {
                    if (request.LastGoodPlaylist is { Length: > 0 } good)
                    {
                        Log(request, observation, decision, outcome: "serve-last-good (deferral budget)");
                        return PlaylistReloadOutcome.ServeLastGood(good);
                    }

                    var kind = PlaylistReloadEvaluation.ClassifyFailure(last.StatusCode, last.Body);
                    Log(request, observation, decision, outcome: "give-up (deferral budget, no last-good)");
                    return PlaylistReloadOutcome.Failed(
                        kind,
                        PlaylistReloadEvaluation.GiveUpMessage(kind, last.StatusCode, "deferral budget exhausted"),
                        last.StatusCode);
                }
            }

            // Always invoke Delay, including TimeSpan.Zero (immediate retry),
            // so tests and a fake clock observe the attempt.
            await delay(decision.Delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public static PlaylistReloadOutcome Run(
        IPlaylistReloadRecoveryStrategy strategy,
        PlaylistReloadRequest request,
        Func<CancellationToken, Task<PlaylistReloadFetchResult>> fetch,
        CancellationToken cancellationToken) =>
        RunAsync(strategy, request, fetch, cancellationToken).GetAwaiter().GetResult();

    private static void Log(
        PlaylistReloadRequest request,
        in PlaylistReloadObservation observation,
        in PlaylistReloadDecision decision,
        string outcome)
    {
        if (request.Log is null)
            return;
        var delayMs = decision.Action == PlaylistReloadAction.Retry ? (int)decision.Delay.TotalMilliseconds : (int?)null;
        request.Log(PlaylistReloadLogging.Format(
            request.Row, observation.Attempt, observation.StatusCode, observation.ContentType,
            observation.Body.Span, outcome, delayMs));
    }

    private static string Describe(in PlaylistReloadDecision decision) =>
        decision.Action switch
        {
            PlaylistReloadAction.Serve => "serve",
            PlaylistReloadAction.Retry => "retry",
            PlaylistReloadAction.GiveUp => "give-up",
            _ => decision.Action.ToString()
        };
}

public sealed class PlaylistReloadRequest
{
    public required bool IsFirstAttach { get; init; }
    public byte[]? LastGoodPlaylist { get; init; }
    public TimeSpan? MaxHold { get; init; }
    public string? Row { get; init; }
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    public Func<DateTimeOffset>? Clock { get; init; }
    public Action<string>? Log { get; init; }
}

public sealed class PlaylistReloadFetchResult
{
    public required int StatusCode { get; init; }
    public required byte[] Body { get; init; }
    public string? ContentType { get; init; }

    public static PlaylistReloadFetchResult FromStatus(int statusCode, byte[]? body = null, string? contentType = null) =>
        new() { StatusCode = statusCode, Body = body ?? [], ContentType = contentType };
}

public enum PlaylistReloadOutcomeKind
{
    Serve,
    ServeLastGood,
    Failed
}

public sealed class PlaylistReloadOutcome
{
    public required PlaylistReloadOutcomeKind Kind { get; init; }
    public byte[] Body { get; init; } = [];
    public string? ContentType { get; init; }
    public int StatusCode { get; init; }
    public PlaybackErrorKind? ErrorKind { get; init; }
    public string? Message { get; init; }

    public static PlaylistReloadOutcome Serve(byte[] body, string? contentType, int statusCode) =>
        new() { Kind = PlaylistReloadOutcomeKind.Serve, Body = body, ContentType = contentType, StatusCode = statusCode };

    public static PlaylistReloadOutcome ServeLastGood(byte[] body) =>
        new() { Kind = PlaylistReloadOutcomeKind.ServeLastGood, Body = body, StatusCode = 200, ContentType = "application/vnd.apple.mpegurl" };

    public static PlaylistReloadOutcome Failed(PlaybackErrorKind kind, string message, int statusCode) =>
        new() { Kind = PlaylistReloadOutcomeKind.Failed, ErrorKind = kind, Message = message, StatusCode = statusCode };
}

public static class PlaylistReloadLogging
{
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Format(
        string? row,
        int attempt,
        int status,
        string? contentType,
        ReadOnlySpan<byte> body,
        string outcome,
        int? delayMs)
    {
        var rowLabel = string.IsNullOrWhiteSpace(row) ? "(unknown)" : row.Trim();
        var type = string.IsNullOrWhiteSpace(contentType) ? "n/a" : contentType.Trim();
        var prefix = SanitizeBodyPrefix(body);
        var delay = delayMs is { } ms ? $" delay_ms={ms}" : string.Empty;
        return $"[playlist-retry] row=\"{rowLabel}\" attempt={attempt} status={status} content-type={type} body=\"{prefix}\" outcome={outcome}{delay}";
    }

    public static string SanitizeBodyPrefix(ReadOnlySpan<byte> body, int maxChars = 80)
    {
        if (body.IsEmpty)
            return "";
        var text = Encoding.UTF8.GetString(body);
        text = UrlPattern.Replace(text, "https://(redacted)");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length <= maxChars)
            return text;
        return text[..maxChars] + "…";
    }
}
