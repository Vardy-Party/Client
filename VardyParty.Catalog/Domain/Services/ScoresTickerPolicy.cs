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
        if (game.IsPostponed)
        {
            return false;
        }

        // BBC often leaves IsFinished true while a shootout is still live; status
        // text with "pen" (e.g. "Penalties") marks that phase. Completed pens use FT.
        if (IsPenaltyShootoutInProgress(game))
        {
            return true;
        }

        if (game.IsFinished)
        {
            return false;
        }

        return game.IsInProgress || game.IsHalfTime || game.Minute.HasValue;
    }

    public static bool IsFinishedWithScore(Game game) =>
        game.IsFinished
        && game.HomeScore.HasValue
        && game.AwayScore.HasValue
        && !IsPenaltyShootoutInProgress(game);

    public static bool IsUpcoming(Game game)
    {
        if (game.IsFinished || game.IsPostponed || IsPenaltyShootoutInProgress(game))
        {
            return false;
        }

        return !game.IsInProgress && !game.IsHalfTime && !game.Minute.HasValue;
    }

    /// <summary>
    /// True when status indicates an active penalty shootout (not FT after pens).
    /// </summary>
    public static bool IsPenaltyShootoutInProgress(Game game)
    {
        var status = game.StatusText ?? string.Empty;
        return status.Contains("pen", StringComparison.OrdinalIgnoreCase);
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
