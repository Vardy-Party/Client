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
    /// City" / "PENALTIES — Home United v Away City" / "FULL TIME — Home United
    /// 2–2 Away City (5–4 pens)".
    /// </summary>
    public static string Headline(MatchEvent matchEvent) =>
        $"{Kind(matchEvent)} — {Fixture(matchEvent)}";

    /// <summary>
    /// "GOAL" / "PENALTY" / "EXTRA TIME" / "PENALTIES" / "FULL TIME"; a neutral
    /// "MATCH" for any kind added later, so a new event can never masquerade as
    /// an existing one.
    /// </summary>
    public static string Kind(MatchEvent matchEvent) => matchEvent.Kind switch
    {
        MatchEventKind.Goal when matchEvent.IsPenaltyGoal => "PENALTY",
        MatchEventKind.Goal => "GOAL",
        MatchEventKind.ExtraTime => "EXTRA TIME",
        MatchEventKind.Penalties => "PENALTIES",
        MatchEventKind.FullTime => "FULL TIME",
        _ => "MATCH",
    };

    /// <summary>
    /// The fixture alone, home team FIRST and away team LAST: "Home United
    /// 2–1 Away City" / "Home United v Away City". Banners sit the home badge
    /// immediately before this text and the away badge immediately after it,
    /// so each badge is next to its own team (the kind label goes elsewhere).
    /// </summary>
    /// <remarks>
    /// Homepage-toast wording: the score for a goal or full time, "v" otherwise.
    /// In-player banners use <see cref="Scoreline"/>.
    /// </remarks>
    public static string Fixture(MatchEvent matchEvent) => matchEvent.Kind switch
    {
        MatchEventKind.Goal or MatchEventKind.FullTime => Scoreline(matchEvent),
        MatchEventKind.ExtraTime or MatchEventKind.Penalties => Versus(matchEvent),
        _ => Versus(matchEvent),
    };

    /// <summary>
    /// "Home United 1–1 Away City" for EVERY kind - the in-player banners
    /// (Android, Windows) have always shown the score for extra time and
    /// penalties too. Home team first, away team last. Penalty-shoot-out
    /// goals keep the regulation score and append <c>(pens H–A)</c>. Full time
    /// appends <c> (H–A pens)</c> when both shoot-out scores are present.
    /// </summary>
    public static string Scoreline(MatchEvent matchEvent)
    {
        var game = matchEvent.Game;
        if (matchEvent.IsPenaltyGoal)
        {
            var regulationHome = game.HomeScore?.ToString() ?? "-";
            var regulationAway = game.AwayScore?.ToString() ?? "-";
            return $"{game.DisplayHome} {regulationHome}–{regulationAway} {game.DisplayAway} (pens {matchEvent.HomeScore}–{matchEvent.AwayScore})";
        }

        var line = $"{game.DisplayHome} {matchEvent.HomeScore}–{matchEvent.AwayScore} {game.DisplayAway}";
        if (matchEvent.Kind == MatchEventKind.FullTime
            && game.PenaltyHomeScore is { } penHome
            && game.PenaltyAwayScore is { } penAway)
        {
            line += $" ({penHome}–{penAway} pens)";
        }

        return line;
    }

    private static string Versus(MatchEvent matchEvent) =>
        $"{matchEvent.Game.DisplayHome} v {matchEvent.Game.DisplayAway}";

    /// <summary>League name for the toast's attribution line.</summary>
    public static string LeagueName(MatchEvent matchEvent) =>
        string.IsNullOrWhiteSpace(matchEvent.Game.DisplayLeague)
            ? HomeRowsBuilder.FallbackLeague
            : matchEvent.Game.DisplayLeague;
}
