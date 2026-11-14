namespace VardyParty.Playback;

/// <summary>
/// Mid-play playlist reload recovery. Platform fetchers call
/// <see cref="IPlaylistReloadRecoveryStrategy.Decide"/> and must not hand a
/// bad body to the player. A later option (temporary switch + auto-return)
/// can be selected by name without changing those fetchers.
/// </summary>
public static class PlaylistReloadStrategyNames
{
    /// <summary>Option 1: retry the same URL in the fetch layer (default).</summary>
    public const string RetrySameUrl = "RetrySameUrl";
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
    public byte[]? LastGoodPlaylist { get; set; }
    public PlaybackErrorKind? LastGiveUpKind { get; private set; }
    public string? LastGiveUpMessage { get; private set; }
    public DateTimeOffset? FirstUnresolvedFailureAt { get; private set; }
    public DateTimeOffset? LastHeldLastGoodAt { get; private set; }

    public void NoteServed(byte[] body)
    {
        FirstPlaylistCompleted = true;
        LastGoodPlaylist = body;
        LastGiveUpKind = null;
        LastGiveUpMessage = null;
        FirstUnresolvedFailureAt = null;
        LastHeldLastGoodAt = null;
    }

    public void NoteGiveUp(PlaybackErrorKind kind, string? message)
    {
        LastGiveUpKind = kind;
        LastGiveUpMessage = message;
    }

    public void NoteFailure(DateTimeOffset now) =>
        FirstUnresolvedFailureAt ??= now;

    public void NoteHeldLastGood(DateTimeOffset now)
    {
        NoteFailure(now);
        LastHeldLastGoodAt = now;
    }

    public bool ShouldGiveUpHeldReload(DateTimeOffset now, TimeSpan budget) =>
        FirstUnresolvedFailureAt is { } started && now - started >= budget;

    /// <summary>
    /// ExoPlayer may raise <c>PlaylistStuckException</c> while we are holding or
    /// re-serving the last-good playlist. Reloading the MediaSource then causes a
    /// visible stall; ignore the stuck error until the give-up budget elapses.
    /// </summary>
    public bool ShouldSuppressStuckPlaylistReload(DateTimeOffset now, TimeSpan giveUpBudget) =>
        FirstUnresolvedFailureAt is { } started
        && now - started < giveUpBudget
        && (LastGoodPlaylist is { Length: > 0 } || LastHeldLastGoodAt is not null);
}
