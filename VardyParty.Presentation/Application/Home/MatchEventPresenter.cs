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

    /// <summary>
    /// "GOAL" / "EXTRA TIME" / "PENALTIES"; a neutral "MATCH" for any kind
    /// added later, so a new event can never masquerade as an existing one.
    /// </summary>
    public static string Kind(MatchEvent matchEvent) => matchEvent.Kind switch
    {
        MatchEventKind.Goal => "GOAL",
        MatchEventKind.ExtraTime => "EXTRA TIME",
        MatchEventKind.Penalties => "PENALTIES",
        _ => "MATCH",
    };

    /// <summary>
    /// The fixture alone, home team FIRST and away team LAST: "Home United
    /// 2–1 Away City" / "Home United v Away City". Banners sit the home badge
    /// immediately before this text and the away badge immediately after it,
    /// so each badge is next to its own team (the kind label goes elsewhere).
    /// </summary>
    /// <remarks>
    /// Homepage-toast wording: the score for a goal, "v" otherwise (unchanged
    /// from the original headline). In-player banners use <see cref="Scoreline"/>.
    /// </remarks>
    public static string Fixture(MatchEvent matchEvent) => matchEvent.Kind switch
    {
        MatchEventKind.Goal => Scoreline(matchEvent),
        MatchEventKind.ExtraTime or MatchEventKind.Penalties => Versus(matchEvent),
        _ => Versus(matchEvent),
    };

    /// <summary>
    /// "Home United 1–1 Away City" for EVERY kind - the in-player banners
    /// (Android, Windows) have always shown the score for extra time and
    /// penalties too. Home team first, away team last.
    /// </summary>
    public static string Scoreline(MatchEvent matchEvent)
    {
        var game = matchEvent.Game;
        return $"{game.DisplayHome} {matchEvent.HomeScore}–{matchEvent.AwayScore} {game.DisplayAway}";
    }

    private static string Versus(MatchEvent matchEvent) =>
        $"{matchEvent.Game.DisplayHome} v {matchEvent.Game.DisplayAway}";

    /// <summary>League name for the toast's attribution line.</summary>
    public static string LeagueName(MatchEvent matchEvent) =>
        string.IsNullOrWhiteSpace(matchEvent.Game.DisplayLeague)
            ? HomeRowsBuilder.FallbackLeague
            : matchEvent.Game.DisplayLeague;
}
