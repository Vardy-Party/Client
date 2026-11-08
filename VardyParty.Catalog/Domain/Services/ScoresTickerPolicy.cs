using System.Text.RegularExpressions;
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
public static partial class ScoresTickerPolicy
{
    /// <summary>
    /// Whole-token match for an active shootout. Substring <c>pen</c> is too loose
    /// (<c>pending</c>, <c>Open</c>). Aligns with BBC emissions (<c>Penalties</c>, <c>pens</c>).
    /// </summary>
    [GeneratedRegex(@"\b(?:pens?|penalties)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PenaltyShootoutToken();

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

        // BBC often leaves IsFinished true while a shootout is still live; whole-token
        // pens/penalties marks that phase. Completed pens use FT.
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
    /// Matches whole tokens only (<c>pen</c>/<c>pens</c>/<c>penalties</c>), never
    /// substrings inside <c>pending</c> or <c>Open</c>.
    /// </summary>
    public static bool IsPenaltyShootoutInProgress(Game game)
    {
        var status = game.StatusText ?? string.Empty;
        return PenaltyShootoutToken().IsMatch(status);
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
