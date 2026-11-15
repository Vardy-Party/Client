namespace VardyParty.Playback;

/// <summary>
/// Mid-play playlist reload recovery. Platform fetchers call
/// <see cref="IPlaylistReloadRecoveryStrategy.Decide"/> and must not hand a
/// bad body to the player. A later option (temporary switch + auto-return)
/// can be selected by name without changing those fetchers.
/// </summary>
public static class PlaylistReloadStrategyNames
{
    /// <summary>Option 1: retry the same URL in the fetch layer only.</summary>
    public const string RetrySameUrl = "RetrySameUrl";

    /// <summary>Option 1 then option 2: retry, switch to keep video going, then auto-return (default).</summary>
    public const string RetrySameUrlThenSwitchBack = "RetrySameUrlThenSwitchBack";
}

public enum PlaylistReloadAction
{
    /// <summary>Give the fetched body to the player.</summary>
    Serve,

    /// <summary>Hold the player request and fetch the same URL again after <see cref="PlaylistReloadDecision.Delay"/>.</summary>
    Retry,

    /// <summary>Stop retrying. Surface <see cref="PlaylistReloadDecision.ErrorKind"/>; do not give the body to the player.</summary>
    GiveUp
}

/// <summary>One playlist fetch the strategy may accept, retry, or reject.</summary>
public readonly record struct PlaylistReloadObservation(
    int Attempt,
    bool IsFirstAttach,
    int StatusCode,
    ReadOnlyMemory<byte> Body,
    string? ContentType);

public readonly record struct PlaylistReloadDecision(
    PlaylistReloadAction Action,
    TimeSpan Delay,
    PlaybackErrorKind? ErrorKind,
    string Reason)
{
    public static PlaylistReloadDecision Serve() =>
        new(PlaylistReloadAction.Serve, TimeSpan.Zero, null, "playable playlist");

    public static PlaylistReloadDecision Retry(TimeSpan delay, string reason) =>
        new(PlaylistReloadAction.Retry, delay, null, reason);

    public static PlaylistReloadDecision GiveUp(PlaybackErrorKind kind, string reason) =>
        new(PlaylistReloadAction.GiveUp, TimeSpan.Zero, kind, reason);
}

public interface IPlaylistReloadRecoveryStrategy
{
    string Name { get; }

    PlaylistReloadDecision Decide(in PlaylistReloadObservation observation);
}

/// <summary>
/// Per-attach state shared by every data source the player opens for this
/// stream. First-attach is the first playlist fetch; later reloads are mid-play.
/// </summary>
public sealed class PlaylistReloadSession
{
    public PlaylistReloadSession(IPlaylistReloadRecoveryStrategy? strategy = null, string? row = null)
    {
        Strategy = strategy ?? PlaylistReloadStrategies.Default;
        Row = row;
    }

    public IPlaylistReloadRecoveryStrategy Strategy { get; }
    public string? Row { get; set; }
    public bool FirstPlaylistCompleted { get; set; }
    public PlaybackErrorKind? LastGiveUpKind { get; private set; }
    public string? LastGiveUpMessage { get; private set; }
    public DateTimeOffset? FirstUnresolvedFailureAt { get; private set; }

    public void NoteServed()
    {
        FirstPlaylistCompleted = true;
        LastGiveUpKind = null;
        LastGiveUpMessage = null;
        FirstUnresolvedFailureAt = null;
    }

    public void NoteGiveUp(PlaybackErrorKind kind, string? message)
    {
        LastGiveUpKind = kind;
        LastGiveUpMessage = message;
    }

    public void NoteFailure(DateTimeOffset now) =>
        FirstUnresolvedFailureAt ??= now;

    public bool ShouldGiveUpHeldReload(DateTimeOffset now, TimeSpan budget) =>
        FirstUnresolvedFailureAt is { } started && now - started >= budget;

    /// <summary>
    /// The player's played-segment memory (<see cref="PlaybackSessionController.PlayedSegments"/>).
    /// Null disables no-replay trimming (tests, tools).
    /// </summary>
    public PlayedSegmentMemory? PlayedSegments { get; init; }

    private int _attachTrimDone;

    /// <summary>Records a fetched media segment in <see cref="PlayedSegments"/>.</summary>
    public void NoteSegmentFetched(string? url) => PlayedSegments?.NoteSegment(url);

    /// <summary>
    /// No-replay rule (all platforms): the first media playlist served for this attach,
    /// or the next one after <see cref="PlayedSegmentMemory.ArmTrimOnce"/>, drops the
    /// leading segments already fetched this viewing. A master playlist passes through
    /// untouched and does not use up the first-media-playlist trim.
    /// </summary>
    public byte[] DropPlayedSegments(byte[] body, string? playlistUrl, out int dropped)
    {
        ArgumentNullException.ThrowIfNull(body);
        dropped = 0;
        var memory = PlayedSegments;
        if (memory is null || body.Length == 0)
            return body;

        var text = System.Text.Encoding.UTF8.GetString(body);
        if (!text.AsSpan().TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal)
            || !text.Contains("#EXTINF", StringComparison.OrdinalIgnoreCase))
        {
            return body;
        }

        var firstMediaPlaylist = Interlocked.Exchange(ref _attachTrimDone, 1) == 0;
        var armed = memory.ConsumeTrim();
        if (!firstMediaPlaylist && !armed)
            return body;

        var trimmed = UnplayedPlaylist.TrimPlayedPrefix(text, memory.WasPlayed, playlistUrl);
        if (trimmed.Dropped <= 0)
            return body;

        dropped = trimmed.Dropped;
        return System.Text.Encoding.UTF8.GetBytes(trimmed.Text);
    }
}
