using VardyParty.Kernel;

namespace VardyParty.Catalog;

/// <summary>
/// Plain-text scores ticker copy shared by native players. Matches the
/// Android in-player ticker (Windows renders the same filters as rich
/// <see cref="TickerDisplayPart"/> runs). Platforms only scroll/paint.
/// </summary>
public readonly record struct ScoresTickerSnapshot(string Title, string Message, string FullText);

public static class ScoresTickerText
{
    public static ScoresTickerSnapshot Build(
        ScoresTickerMode mode,
        IEnumerable<Game> games,
        string? watchedLeague,
        string? watchedHome,
        string? watchedAway)
    {
        var list = games as IList<Game> ?? games.ToList();
        var title = TitleFor(mode, watchedLeague);
        var lines = mode switch
        {
            ScoresTickerMode.AllLeaguesInPlay => BuildAllLeaguesInPlay(list),
            ScoresTickerMode.AllFinished => BuildFinished(list),
            ScoresTickerMode.AllUpcoming => BuildUpcoming(list),
            _ => BuildSameLeagueInPlay(list, watchedLeague, watchedHome, watchedAway)
        };

        var empty = mode switch
        {
            ScoresTickerMode.AllLeaguesInPlay => "No in-play games right now.",
            ScoresTickerMode.AllFinished => "No finished games right now.",
            ScoresTickerMode.AllUpcoming => "No remaining unstarted games today.",
            _ => "No other in-play games in this league right now."
        };

        var message = lines.Count == 0
            ? empty
            : string.Join(InternationalTeamDisplay.TickerSeparator, lines);
        return new ScoresTickerSnapshot(title, message, $"{title}: {message}");
    }

    public static string TitleFor(ScoresTickerMode mode, string? watchedLeague) => mode switch
    {
        ScoresTickerMode.AllLeaguesInPlay => "All leagues in-play",
        ScoresTickerMode.AllFinished => "Finished games",
        ScoresTickerMode.AllUpcoming => "Upcoming games",
        _ => string.IsNullOrWhiteSpace(watchedLeague)
            ? "In-play games"
            : $"In-play: {watchedLeague}"
    };

    private static List<string> BuildSameLeagueInPlay(
        IEnumerable<Game> games,
        string? watchedLeague,
        string? watchedHome,
        string? watchedAway)
    {
        return games
            .Where(g => ScoresTickerPolicy.IsSameLeague(g, watchedLeague))
            .Where(ScoresTickerPolicy.IsInPlay)
            .Where(g => !IsWatchedGame(g, watchedHome, watchedAway))
            .OrderByDescending(g => g.LiveMinuteForOrdering)
            .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
            .Select(FormatLiveLine)
            .ToList();
    }

    private static List<string> BuildAllLeaguesInPlay(IEnumerable<Game> games) =>
        games
            .Where(ScoresTickerPolicy.IsInPlay)
            .OrderBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(g => g.LiveMinuteForOrdering)
            .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"[{g.DisplayLeague}] {FormatLiveLine(g)}")
            .ToList();

    private static List<string> BuildFinished(IEnumerable<Game> games) =>
        games
            .Where(ScoresTickerPolicy.IsFinishedWithScore)
            .OrderBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(g => g.StartUtcForOrdering)
            .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"[{g.DisplayLeague}] {FormatLiveLine(g)}")
            .ToList();

    private static List<string> BuildUpcoming(IEnumerable<Game> games) =>
        games
            .Where(ScoresTickerPolicy.IsUpcoming)
            .OrderBy(g => g.StartUtcForOrdering)
            .ThenBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"[{g.DisplayLeague}] {FormatUpcomingLine(g)}")
            .ToList();

    private static bool IsWatchedGame(Game game, string? home, string? away)
    {
        if (string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(away))
        {
            return false;
        }

        return (SameTeam(game.DisplayHome, home) && SameTeam(game.DisplayAway, away))
            || (SameTeam(game.DisplayHome, away) && SameTeam(game.DisplayAway, home));
    }

    private static bool SameTeam(string? left, string? right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static string FormatScore(Game game)
    {
        var homeScore = game.HomeScore?.ToString() ?? "-";
        var awayScore = game.AwayScore?.ToString() ?? "-";
        var score = $"{homeScore}-{awayScore}";
        if (game.AggregateHomeScore.HasValue || game.AggregateAwayScore.HasValue)
        {
            var aggregateHome = game.AggregateHomeScore?.ToString() ?? "-";
            var aggregateAway = game.AggregateAwayScore?.ToString() ?? "-";
            score += $" agg {aggregateHome}-{aggregateAway}";
        }

        return score;
    }

    private static string FormatLiveLine(Game game)
    {
        var status = game.DisplayStatusText();
        if (string.IsNullOrWhiteSpace(status))
        {
            status = game.IsFinished ? "FT" : "Live";
        }

        var international = InternationalTeamDisplay.IsInternationalGame(game);
        var home = InternationalTeamDisplay.FormatTeamName(game.DisplayHome, international);
        var away = InternationalTeamDisplay.FormatTeamName(game.DisplayAway, international);
        return $"{home} {FormatScore(game)} {away} ({status})";
    }

    private static string FormatUpcomingLine(Game game)
    {
        var localKickoff = game.Start.Kind == DateTimeKind.Local ? game.Start : game.Start.ToLocalTime();
        var kickoffText = localKickoff == default ? "TBD" : localKickoff.ToString("HH:mm");
        var international = InternationalTeamDisplay.IsInternationalGame(game);
        var home = InternationalTeamDisplay.FormatTeamName(game.DisplayHome, international);
        var away = InternationalTeamDisplay.FormatTeamName(game.DisplayAway, international);
        return $"{kickoffText} {home} vs {away}";
    }
}
