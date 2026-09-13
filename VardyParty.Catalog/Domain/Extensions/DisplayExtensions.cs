using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;

namespace VardyParty.Catalog;

public static class DisplayExtensions
{
    /// <summary>
    /// Convert enriched API games dictionary (league -> games) into an ordered list of games
    /// suitable for homepage rendering. Live and upcoming fixtures stay visible; scored
    /// full-time results stay for a short window. Scoreless heuristic-FT is omitted.
    /// Do not use this list as the in-player ticker catalog — tickers flatten the raw snapshot.
    /// </summary>
    public static List<Game> ToDisplay(this IDictionary<string, List<Game>>? source)
    {
        if (source == null) return new List<Game>();

        var allGames = source.SelectMany(kvp => kvp.Value ?? new List<Game>()).Where(g => g != null).ToList();

        var now = DateTime.UtcNow;

        var visible = allGames
            .Where(g => !g.IsFinished || ScoresTickerPolicy.IsRecentScoredFinish(g, now))
            .Where(g => BbcFixtureSchedule.IsWithinLookAheadWindow(g.StartUtcForOrdering, now))
            .Where(g => g.IsLiveForOrdering
                || ScoresTickerPolicy.IsRecentScoredFinish(g, now)
                || g.IsScheduledUpcoming(now)
                || g.StartUtcForOrdering == default
                || g.StartUtcForOrdering == DateTime.MaxValue
                || g.StartUtcForOrdering > now.AddHours(-3))
            .ToList();

        var ordered = visible
            .OrderBy(g => g.IsOlympicLeague ? 1 : 0)
            .ThenBy(g => g.SortTierForOrdering)
            .ThenByDescending(g => g.LiveMinuteForOrdering)
            .ThenBy(g => g.StartUtcForOrdering)
            .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return ordered;
    }
}
