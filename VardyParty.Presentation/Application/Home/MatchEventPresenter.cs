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
    public static string Headline(MatchEvent matchEvent) =>
        $"{Kind(matchEvent)} — {Fixture(matchEvent)}";

    /// <summary>"GOAL" / "EXTRA TIME" / "PENALTIES".</summary>
    public static string Kind(MatchEvent matchEvent) => matchEvent.Kind switch
    {
        MatchEventKind.Goal => "GOAL",
        MatchEventKind.ExtraTime => "EXTRA TIME",
        _ => "PENALTIES",
    };

    /// <summary>
    /// The fixture alone, home team FIRST and away team LAST: "Home United
    /// 2–1 Away City" / "Home United v Away City". Banners sit the home badge
    /// immediately before this text and the away badge immediately after it,
    /// so each badge is next to its own team (the kind label goes elsewhere).
    /// </summary>
    public static string Fixture(MatchEvent matchEvent)
    {
        var game = matchEvent.Game;
        return matchEvent.Kind == MatchEventKind.Goal
            ? $"{game.DisplayHome} {matchEvent.HomeScore}–{matchEvent.AwayScore} {game.DisplayAway}"
            : $"{game.DisplayHome} v {game.DisplayAway}";
    }

    /// <summary>League name for the toast's attribution line.</summary>
    public static string LeagueName(MatchEvent matchEvent) =>
        string.IsNullOrWhiteSpace(matchEvent.Game.DisplayLeague)
            ? HomeRowsBuilder.FallbackLeague
            : matchEvent.Game.DisplayLeague;
}
