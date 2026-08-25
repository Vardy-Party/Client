using VardyParty.Models;

namespace VardyParty.Catalog;

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

    public void MarkUserInitiated() => UserInitiatedResolution = true;

    public void ClearUserInitiation() => UserInitiatedResolution = false;

    public static bool SameGame(Game? a, Game? b)
    {
        if (a is null || b is null)
            return false;

        return string.Equals(a.Home, b.Home, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Away, b.Away, StringComparison.OrdinalIgnoreCase);
    }

    public static Game? FindMatchingGame(IReadOnlyList<Game> games, Game target) =>
        games.FirstOrDefault(g => SameGame(g, target));

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

        if (resolutionExhausted)
            return ResumeAfterPlayerAction.Clear;

        if (currentGame != null && ReferenceEquals(currentGame, selectedGame))
            return ResumeAfterPlayerAction.Resume;

        return ResumeAfterPlayerAction.Clear;
    }
}
