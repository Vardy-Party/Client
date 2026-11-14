using System.Text;
using System.Text.RegularExpressions;
using VardyParty.Kernel;
using VardyParty.Ports;

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
    /// Shared mid-play hold (see <see cref="PlaybackPolicy.PlaylistReloadHoldBudget"/>).
    /// If the next retry delay would exceed the remaining budget, give up as
    /// CDN-unreachable so the session can switch (option 2). Never serve a stale
    /// last-good playlist — that re-shows already-watched segments.
    /// </summary>
    public static readonly TimeSpan WindowsDeferralBudget = PlaybackPolicy.PlaylistReloadHoldBudget;

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

            request.Session?.NoteFailure(clock());

            if (decision.Action == PlaylistReloadAction.Retry
                && request.Session?.ShouldGiveUpHeldReload(clock(), PlaybackPolicy.PlaylistReloadGiveUpBudget) == true)
            {
                var kind = decision.ErrorKind ?? PlaylistReloadEvaluation.ClassifyFailure(last.StatusCode, last.Body);
                var message = PlaylistReloadEvaluation.GiveUpMessage(kind, last.StatusCode, "playlist reload time budget exhausted");
                Log(request, observation, decision, outcome: "give-up (time budget)");
                request.Session?.NoteGiveUp(kind, message);
                return PlaylistReloadOutcome.Failed(kind, message, last.StatusCode);
            }

            if (decision.Action == PlaylistReloadAction.GiveUp)
            {
                var kind = decision.ErrorKind ?? PlaylistReloadEvaluation.ClassifyFailure(last.StatusCode, last.Body);
                var message = PlaylistReloadEvaluation.GiveUpMessage(kind, last.StatusCode, decision.Reason);
                request.Session?.NoteGiveUp(kind, message);
                return PlaylistReloadOutcome.Failed(kind, message, last.StatusCode);
            }

            // Retry: maybe the player request cannot hold the delay (Windows AMS
            // deferral, Android Open). Never escape by serving a stale playlist.
            if (request.MaxHold is { } budget)
            {
                var remaining = budget - (clock() - started);
                if (remaining < decision.Delay)
                {
                    var kind = PlaylistReloadEvaluation.ClassifyFailure(last.StatusCode, last.Body);
                    var message = PlaylistReloadEvaluation.GiveUpMessage(kind, last.StatusCode, "deferral budget exhausted");
                    Log(request, observation, decision, outcome: "give-up (deferral budget)");
                    request.Session?.NoteGiveUp(kind, message);
                    return PlaylistReloadOutcome.Failed(kind, message, last.StatusCode);
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
        var line = PlaylistReloadLogging.Format(
            request.Row, observation.Attempt, observation.StatusCode, observation.ContentType,
            observation.Body.Span, outcome, delayMs);
        request.Log(PlaylistReloadLogging.IsInfo(observation.Attempt, outcome), line);
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
    /// <summary>
    /// First argument is true for Information (attempt &gt; 1 or outcome is not a first-try serve).
    /// Routine mid-play <c>attempt=1 status=200 serve</c> is Debug (false).
    /// </summary>
    public Action<bool, string>? Log { get; init; }
    public PlaylistReloadSession? Session { get; init; }

    /// <summary>
    /// Mid-play request used by Android, Windows, and Linux: no last-good
    /// playlist, shared <see cref="PlaybackPolicy.PlaylistReloadHoldBudget"/>.
    /// </summary>
    public static PlaylistReloadRequest MidPlay(
        bool isFirstAttach,
        PlaylistReloadSession? session,
        string? row,
        Action<bool, string>? log = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null) =>
        new()
        {
            IsFirstAttach = isFirstAttach,
            LastGoodPlaylist = null,
            MaxHold = PlaybackPolicy.PlaylistReloadHoldBudget,
            Row = row,
            Session = session,
            Log = log,
            Delay = delay,
            Clock = clock
        };
}

public sealed class PlaylistReloadFetchResult
{
    public required int StatusCode { get; init; }
    public required byte[] Body { get; init; }
    public string? ContentType { get; init; }

    public static PlaylistReloadFetchResult FromStatus(int statusCode, byte[]? body = null, string? contentType = null) =>
        new() { StatusCode = statusCode, Body = body ?? [], ContentType = contentType };

    /// <summary>
    /// A completed 200/206 with an empty body is a dropped response, not a
    /// playlist: treat it as <c>status=0</c> so give-up stays <see cref="PlaybackErrorKind.CdnUnreachable"/>.
    /// </summary>
    public static PlaylistReloadFetchResult FromHttp(int statusCode, byte[]? body = null, string? contentType = null)
    {
        body ??= [];
        if (statusCode is 200 or 206 && body.Length == 0)
            return new() { StatusCode = 0, Body = [], ContentType = contentType };
        return new() { StatusCode = statusCode, Body = body, ContentType = contentType };
    }
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

    /// <summary>
    /// Routine first-try serves stay at Debug. Retries, give-up, and any later
    /// attempt are Information — including a recovered <c>attempt=2 serve</c>.
    /// Outcome is compared exactly to <c>serve</c>.
    /// </summary>
    public static bool IsInfo(int attempt, string outcome) =>
        attempt > 1 || !string.Equals(outcome, "serve", StringComparison.Ordinal);
}

/// <summary>Display name for <c>[playlist-retry] row="..."</c> (the overlay channel label).</summary>
public static class PlaylistReloadRowName
{
    public static string? ForUrl(IStreamSwitchingService? switching, string? url)
    {
        var row = PlaybackRowRequestContext.FindRow(switching, url) ?? switching?.GetCurrentStream();
        return ForRow(row);
    }

    public static string? ForRow(EnrichedStream? row) =>
        PlayerOverlayFormatter.ResolveChipOrChannelLabel(row?.Stream)
        ?? row?.Stream?.Channel;
}
