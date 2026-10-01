namespace VardyParty.Presentation;

/// <summary>
/// Where D-pad focus lands when the user leaves a stream and the homepage
/// is showing again. The watched fixture if it is still on the board;
/// otherwise the first card of the first rail that still has a game.
/// Never the header Menu.
/// </summary>
public static class PlaybackExitFocus
{
    /// <param name="WatchedGame">
    /// True when <see cref="RailIndex"/>/<see cref="CardIndex"/> is the
    /// fixture that was playing. False when that fixture has left the board
    /// and the indices are the fallback card.
    /// </param>
    public readonly record struct Choice(bool WatchedGame, int RailIndex, int CardIndex);

    /// <summary>
    /// <paramref name="rails"/> is each rail's game keys in visual order
    /// (<see cref="HomeBoardDiffer.GameKey"/>).
    /// </summary>
    public static Choice? Choose(string? watchedGameKey, IReadOnlyList<IReadOnlyList<string>> rails)
    {
        if (!string.IsNullOrEmpty(watchedGameKey))
        {
            for (var rail = 0; rail < rails.Count; rail++)
            {
                var cards = rails[rail];
                for (var card = 0; card < cards.Count; card++)
                {
                    if (string.Equals(cards[card], watchedGameKey, StringComparison.Ordinal))
                    {
                        return new Choice(WatchedGame: true, rail, card);
                    }
                }
            }
        }

        for (var rail = 0; rail < rails.Count; rail++)
        {
            if (rails[rail].Count > 0)
            {
                return new Choice(WatchedGame: false, rail, 0);
            }
        }

        return null;
    }
}
