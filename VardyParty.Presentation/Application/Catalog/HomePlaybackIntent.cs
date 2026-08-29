using VardyParty.Kernel;

namespace VardyParty.Presentation;

public enum ResumeAfterPlayerAction
{
    None,
    Resume,
    Clear
}

/// <summary>
/// Home click vs resume-after-player rules. Never auto-selects <c>games[0]</c>.
/// </summary>
public sealed class HomePlaybackIntent
{
    public bool UserInitiatedResolution { get; private set; }

    public bool PlayerSessionStarted { get; private set; }

    public void MarkUserInitiated()
    {
        UserInitiatedResolution = true;
        PlayerSessionStarted = false;
    }

    public void MarkPlayerSessionStarted() => PlayerSessionStarted = true;

    public void ClearUserInitiation()
    {
        UserInitiatedResolution = false;
        PlayerSessionStarted = false;
    }

    public static bool SameGame(Game? a, Game? b)
    {
        if (a is null || b is null)
            return false;

        return string.Equals(a.Home, b.Home, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Away, b.Away, StringComparison.OrdinalIgnoreCase);
    }

    public static Game? FindMatchingGame(IReadOnlyList<Game> games, Game target) =>
        games.FirstOrDefault(g => SameGame(g, target));

    /// <summary>
    /// Whether a card pick that lands while a resolution attempt still looks
    /// active should be swallowed. Only a genuinely in-flight attempt for the
    /// SAME game may be ignored; once the previous attempt has delivered its
    /// outcome (<paramref name="resolutionExhausted"/>) a re-click must start
    /// a fresh resolution — the field dead-end was this guard eating every
    /// re-click after a no-working-streams outcome left the selection latched.
    /// </summary>
    public static bool ShouldIgnoreRepick(bool sameGame, bool resolutionExhausted) =>
        sameGame && !resolutionExhausted;

    public bool IsSelected(Game game, Game? selectedGame) =>
        selectedGame != null && SameGame(game, selectedGame);

    /// <summary>
    /// Rebinds an explicit OK/click selection onto refreshed game instances.
    /// Does not pick the first card when nothing was chosen.
    /// </summary>
    public static (Game? Selected, Game? Current) RebindSelection(IReadOnlyList<Game> games, Game? selectedGame)
    {
        if (games.Count == 0)
            return (null, null);

        if (selectedGame != null)
        {
            var match = FindMatchingGame(games, selectedGame);
            return (match, match);
        }

        return (null, null);
    }

    public ResumeAfterPlayerAction DecideResumeAfterPlayer(
        bool isResolvingStreams,
        Game? selectedGame,
        Game? currentGame,
        bool resolutionExhausted)
    {
        if (isResolvingStreams || selectedGame is null || !UserInitiatedResolution)
            return ResumeAfterPlayerAction.None;

        if (!PlayerSessionStarted)
            return ResumeAfterPlayerAction.None;

        if (resolutionExhausted)
            return ResumeAfterPlayerAction.Clear;

        if (currentGame != null && ReferenceEquals(currentGame, selectedGame))
            return ResumeAfterPlayerAction.Resume;

        return ResumeAfterPlayerAction.Clear;
    }
}
