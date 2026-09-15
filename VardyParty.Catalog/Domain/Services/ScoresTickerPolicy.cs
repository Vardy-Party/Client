using VardyParty.Kernel;

namespace VardyParty.Catalog;

public enum ScoresTickerMode
{
    SameLeagueInPlay,
    AllLeaguesInPlay,
    AllFinished,
    AllUpcoming
}

/// <summary>
/// Shared ticker filter/cycle rules. Platforms only render.
/// </summary>
public static class ScoresTickerPolicy
{
    public static ScoresTickerMode Next(ScoresTickerMode current) => current switch
    {
        ScoresTickerMode.SameLeagueInPlay => ScoresTickerMode.AllLeaguesInPlay,
        ScoresTickerMode.AllLeaguesInPlay => ScoresTickerMode.AllFinished,
        ScoresTickerMode.AllFinished => ScoresTickerMode.AllUpcoming,
        _ => ScoresTickerMode.SameLeagueInPlay
    };

    public static bool IsInPlay(Game game)
    {
        if (game.IsFinished || game.IsPostponed)
        {
            return false;
        }

        return game.IsInProgress || game.IsHalfTime || game.Minute.HasValue;
    }

    public static bool IsFinishedWithScore(Game game) =>
        game.IsFinished && game.HomeScore.HasValue && game.AwayScore.HasValue;

    /// <summary>
    /// Homepage cards keep scored FT results for the rest of the UTC day or eight
    /// hours after kickoff — long enough to read the score, short enough to drop
    /// yesterday. Scoreless heuristic-FT is not a result.
    /// </summary>
    public static readonly TimeSpan HomepageFinishedRetention = TimeSpan.FromHours(8);

    public static bool IsRecentScoredFinish(Game game, DateTime utcNow)
    {
        if (!IsFinishedWithScore(game))
        {
            return false;
        }

        var start = game.StartUtcForOrdering;
        if (start == default || start == DateTime.MaxValue)
        {
            return true;
        }

        return start > utcNow - HomepageFinishedRetention
            || start.Date == utcNow.Date;
    }

    public static bool IsUpcoming(Game game)
    {
        if (game.IsFinished || game.IsPostponed)
        {
            return false;
        }

        return !game.IsInProgress && !game.IsHalfTime && !game.Minute.HasValue;
    }

    public static bool IsSameLeague(Game game, string? league)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            return true;
        }

        return string.Equals(
            (game.DisplayLeague ?? string.Empty).Trim(),
            league.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }
}
