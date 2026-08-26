using System.Text.RegularExpressions;
using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>Coarse phase of a match, used to pick chip colour and effects.</summary>
public enum MatchPhase
{
    Upcoming,
    Live,
    HalfTime,
    ExtraTime,
    Penalties,
    FullTime,
    Postponed,
}

/// <summary>
/// Turns a <see cref="Game"/> into display strings for the homepage card:
/// status chip (minutes / injury time / HT / FT / extra time / penalties),
/// kick-off time, score and aggregate. Pure and unit-testable.
/// </summary>
public static partial class MatchStatusPresenter
{
    [GeneratedRegex(@"\b(?:ET|AET)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraTimeToken();

    public static MatchPhase GetPhase(Game game)
    {
        if (game.IsPostponed) return MatchPhase.Postponed;
        if (game.IsFinished) return MatchPhase.FullTime;

        var status = game.StatusText ?? string.Empty;
        if (status.Contains("pen", StringComparison.OrdinalIgnoreCase)) return MatchPhase.Penalties;
        if (status.Contains("extra time", StringComparison.OrdinalIgnoreCase) || ExtraTimeToken().IsMatch(status))
        {
            return MatchPhase.ExtraTime;
        }

        if (game.IsHalfTime) return MatchPhase.HalfTime;
        return game.IsLiveForOrdering ? MatchPhase.Live : MatchPhase.Upcoming;
    }

    public static bool IsLivePhase(MatchPhase phase) =>
        phase is MatchPhase.Live or MatchPhase.HalfTime or MatchPhase.ExtraTime or MatchPhase.Penalties;

    /// <summary>Chip text: "45+2'", "HT", "FT", "Pens", "ET" or the kick-off time.</summary>
    public static string GetStatusText(Game game, DateTime? nowLocal = null)
    {
        var phase = GetPhase(game);
        switch (phase)
        {
            case MatchPhase.Postponed:
                return "Postponed";
            case MatchPhase.FullTime:
                return "FT";
            case MatchPhase.HalfTime:
                return "HT";
            case MatchPhase.Penalties:
                return "Pens";
            case MatchPhase.ExtraTime:
                {
                    var text = game.DisplayStatusText();
                    return string.IsNullOrWhiteSpace(text) ? "ET" : text;
                }
            case MatchPhase.Live:
                {
                    var text = game.DisplayStatusText();
                    return string.IsNullOrWhiteSpace(text) ? "Live" : text;
                }
            default:
                return FormatStartTime(game.Start, nowLocal);
        }
    }

    public static string FormatStartTime(DateTime start, DateTime? nowLocal = null)
    {
        if (start == default) return string.Empty;

        var local = start.Kind == DateTimeKind.Local ? start : start.ToLocalTime();
        var today = (nowLocal ?? DateTime.Now).Date;
        if (local.Date == today) return local.ToString("h:mm tt");
        if (local.Date == today.AddDays(1)) return $"Tomorrow {local:h:mm tt}";
        return local.ToString("MMM dd, h:mm tt");
    }

    public static bool HasScore(Game game) => game.HomeScore.HasValue || game.AwayScore.HasValue;

    public static string GetScoreText(Game game) =>
        HasScore(game) ? $"{game.HomeScore?.ToString() ?? "0"} - {game.AwayScore?.ToString() ?? "0"}" : "VS";

    public static string? GetAggregateText(Game game) =>
        game.AggregateHomeScore.HasValue && game.AggregateAwayScore.HasValue
            ? $"Agg {game.AggregateHomeScore}-{game.AggregateAwayScore}"
            : null;
}
