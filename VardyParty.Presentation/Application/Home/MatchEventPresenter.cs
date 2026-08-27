namespace VardyParty.Presentation;

/// <summary>
/// Display strings for a <see cref="MatchEvent"/>: the toast headline gives
/// the sting its visual attribution (field report: "3 notes, no idea what it
/// means"). Pure and unit-testable.
/// </summary>
public static class MatchEventPresenter
{
    /// <summary>
    /// "GOAL — Home United 2–1 Away City" / "EXTRA TIME — Home United v Away
    /// City" / "PENALTIES — Home United v Away City".
    /// </summary>
    public static string Headline(MatchEvent matchEvent)
    {
        var game = matchEvent.Game;
        return matchEvent.Kind switch
        {
            MatchEventKind.Goal =>
                $"GOAL — {game.DisplayHome} {matchEvent.HomeScore}–{matchEvent.AwayScore} {game.DisplayAway}",
            MatchEventKind.ExtraTime =>
                $"EXTRA TIME — {game.DisplayHome} v {game.DisplayAway}",
            _ =>
                $"PENALTIES — {game.DisplayHome} v {game.DisplayAway}",
        };
    }

    /// <summary>League name for the toast's attribution line.</summary>
    public static string LeagueName(MatchEvent matchEvent) =>
        string.IsNullOrWhiteSpace(matchEvent.Game.DisplayLeague)
            ? HomeRowsBuilder.FallbackLeague
            : matchEvent.Game.DisplayLeague;
}
