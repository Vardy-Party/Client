using VardyParty.Models;

namespace VardyParty.Presentation;

/// <summary>
/// Shared home-shell presentation model. Blazor (phase 1) and Avalonia bind to this;
/// MAUI XAML can later. Does not own Auth0 or native player chrome.
/// </summary>
public sealed class HomeShellViewModel
{
    private readonly HomePlaybackIntent _intent = new();

    public Game? SelectedGame { get; private set; }

    public bool UserInitiatedResolution => _intent.UserInitiatedResolution;

    public bool PlayerSessionStarted => _intent.PlayerSessionStarted;

    public void OnUserPicked(Game game)
    {
        _intent.MarkUserInitiated();
        SelectedGame = game;
    }

    public void MarkPlayerSessionStarted() => _intent.MarkPlayerSessionStarted();

    public void ClearSelection()
    {
        SelectedGame = null;
        _intent.ClearUserInitiation();
    }

    public void RebindGames(IReadOnlyList<Game> games)
    {
        var (selected, _) = HomePlaybackIntent.RebindSelection(games, SelectedGame);
        SelectedGame = selected;
    }

    public bool IsSelected(Game game) => _intent.IsSelected(game, SelectedGame);

    public ResumeAfterPlayerAction DecideResumeAfterPlayer(
        bool isResolvingStreams,
        Game? currentGame,
        bool resolutionExhausted) =>
        _intent.DecideResumeAfterPlayer(
            isResolvingStreams,
            SelectedGame,
            currentGame,
            resolutionExhausted);
}
