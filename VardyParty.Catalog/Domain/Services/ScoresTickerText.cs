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
        var selected = SelectGames(mode, games, watchedLeague, watchedHome, watchedAway);
        var title = TitleFor(mode, watchedLeague);
        var lines = selected.Select(g => FormatLine(mode, g)).ToList();
        var message = lines.Count == 0
            ? EmptyMessage(mode)
            : string.Join(InternationalTeamDisplay.TickerSeparator, lines);
        return new ScoresTickerSnapshot(title, message, $"{title}: {message}");
    }

    public static List<TickerDisplayPart> BuildParts(
        ScoresTickerMode mode,
        IEnumerable<Game> games,
        string? watchedLeague,
        string? watchedHome,
        string? watchedAway)
    {
        var selected = SelectGames(mode, games, watchedLeague, watchedHome, watchedAway);
        var title = TitleFor(mode, watchedLeague);
        if (selected.Count == 0)
        {
            return InternationalTeamDisplay.TextParts($"{title}: {EmptyMessage(mode)}").ToList();
        }

        var parts = new List<TickerDisplayPart>();
        parts.AddRange(InternationalTeamDisplay.TextParts($"{title}: "));
        for (var i = 0; i < selected.Count; i++)
        {
            if (i > 0)
            {
                parts.AddRange(InternationalTeamDisplay.SeparatorParts());
            }

            parts.AddRange(FormatLineParts(mode, selected[i]));
        }

        return parts;
    }

    public static IReadOnlyList<Game> SelectGames(
        ScoresTickerMode mode,
        IEnumerable<Game> games,
        string? watchedLeague,
        string? watchedHome,
        string? watchedAway)
    {
        var list = games as IList<Game> ?? games.ToList();
        return mode switch
        {
            ScoresTickerMode.AllLeaguesInPlay => SelectAllLeaguesInPlay(list),
            ScoresTickerMode.AllFinished => SelectFinished(list),
            ScoresTickerMode.AllUpcoming => SelectUpcoming(list),
            _ => SelectSameLeagueInPlay(list, watchedLeague, watchedHome, watchedAway)
        };
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

    public static string EmptyMessage(ScoresTickerMode mode) => mode switch
    {
        ScoresTickerMode.AllLeaguesInPlay => "No in-play games right now.",
        ScoresTickerMode.AllFinished => "No finished games right now.",
        ScoresTickerMode.AllUpcoming => "No remaining unstarted games today.",
        _ => "No other in-play games in this league right now."
    };

    private static List<Game> SelectSameLeagueInPlay(
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
            .ToList();
    }

    private static List<Game> SelectAllLeaguesInPlay(IEnumerable<Game> games) =>
        games
            .Where(ScoresTickerPolicy.IsInPlay)
            .OrderBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(g => g.LiveMinuteForOrdering)
            .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<Game> SelectFinished(IEnumerable<Game> games) =>
        games
            .Where(ScoresTickerPolicy.IsFinishedWithScore)
            .OrderBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(g => g.StartUtcForOrdering)
            .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<Game> SelectUpcoming(IEnumerable<Game> games) =>
        games
            .Where(ScoresTickerPolicy.IsUpcoming)
            .OrderBy(g => g.StartUtcForOrdering)
            .ThenBy(g => g.DisplayLeague, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
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

    private static string FormatLine(ScoresTickerMode mode, Game game) => mode switch
    {
        ScoresTickerMode.AllUpcoming => PrefixLeague(game, FormatUpcomingLine(game)),
        ScoresTickerMode.AllLeaguesInPlay or ScoresTickerMode.AllFinished => PrefixLeague(game, FormatLiveLine(game)),
        _ => FormatLiveLine(game)
    };

    private static IEnumerable<TickerDisplayPart> FormatLineParts(ScoresTickerMode mode, Game game)
    {
        if (mode is ScoresTickerMode.AllLeaguesInPlay or ScoresTickerMode.AllFinished or ScoresTickerMode.AllUpcoming)
        {
            yield return new TickerDisplayPart($"[{game.DisplayLeague}]");
        }

        if (mode == ScoresTickerMode.AllUpcoming)
        {
            foreach (var part in FormatUpcomingLineParts(game))
            {
                yield return part;
            }

            yield break;
        }

        foreach (var part in FormatLiveLineParts(game))
        {
            yield return part;
        }
    }

    private static string PrefixLeague(Game game, string line) => $"[{game.DisplayLeague}] {line}";

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

    private static string StatusText(Game game)
    {
        var status = game.DisplayStatusText();
        if (string.IsNullOrWhiteSpace(status))
        {
            status = game.IsFinished ? "FT" : "Live";
        }

        return status;
    }

    private static string FormatLiveLine(Game game)
    {
        var international = InternationalTeamDisplay.IsInternationalGame(game);
        var home = InternationalTeamDisplay.FormatTeamName(game.DisplayHome, international);
        var away = InternationalTeamDisplay.FormatTeamName(game.DisplayAway, international);
        return $"{home} {FormatScore(game)} {away} ({StatusText(game)})";
    }

    private static IEnumerable<TickerDisplayPart> FormatLiveLineParts(Game game)
    {
        var international = InternationalTeamDisplay.IsInternationalGame(game);
        foreach (var part in InternationalTeamDisplay.TeamParts(game.DisplayHome, international))
        {
            yield return part;
        }

        yield return new TickerDisplayPart($"  {FormatScore(game)}  ");

        foreach (var part in InternationalTeamDisplay.TeamParts(game.DisplayAway, international))
        {
            yield return part;
        }

        yield return new TickerDisplayPart($"  ({StatusText(game)})");
    }

    private static string FormatUpcomingLine(Game game)
    {
        var kickoffText = KickoffText(game);
        var international = InternationalTeamDisplay.IsInternationalGame(game);
        var home = InternationalTeamDisplay.FormatTeamName(game.DisplayHome, international);
        var away = InternationalTeamDisplay.FormatTeamName(game.DisplayAway, international);
        return $"{kickoffText} {home} vs {away}";
    }

    private static IEnumerable<TickerDisplayPart> FormatUpcomingLineParts(Game game)
    {
        yield return new TickerDisplayPart(KickoffText(game));
        var international = InternationalTeamDisplay.IsInternationalGame(game);
        foreach (var part in InternationalTeamDisplay.TeamParts(game.DisplayHome, international))
        {
            yield return part;
        }

        yield return new TickerDisplayPart("vs");
        foreach (var part in InternationalTeamDisplay.TeamParts(game.DisplayAway, international))
        {
            yield return part;
        }
    }

    private static string KickoffText(Game game)
    {
        var localKickoff = game.Start.Kind == DateTimeKind.Local ? game.Start : game.Start.ToLocalTime();
        return localKickoff == default ? "TBD" : localKickoff.ToString("HH:mm");
    }
}
