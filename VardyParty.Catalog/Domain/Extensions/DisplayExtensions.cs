using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;

namespace VardyParty.Catalog;

public static class DisplayExtensions
{
    /// <summary>
    /// Convert enriched API games dictionary (league -> games) into an ordered list of games
    /// suitable for homepage rendering. Live and upcoming fixtures stay visible. A scored
    /// finish stays for <see cref="FinishedGameGrace.Duration"/> after this process first
    /// sees it, then drops. Scoreless heuristic-FT is omitted. Do not use this list as the
    /// in-player ticker catalog — tickers flatten the raw snapshot.
    /// </summary>
    public static List<Game> ToDisplay(this IDictionary<string, List<Game>>? source) =>
        source.ToDisplay(DateTime.UtcNow, FinishedGameGrace.Shared);

    /// <summary>
    /// Same board as <see cref="ToDisplay(IDictionary{string, List{Game}}?)"/> with an
    /// explicit clock and observation store, so tests can age a finish without waiting.
    /// </summary>
    public static List<Game> ToDisplay(
        this IDictionary<string, List<Game>>? source,
        DateTime utcNow,
        FinishedGameGrace grace)
    {
        if (source == null) return new List<Game>();

        var allGames = source.SelectMany(kvp => kvp.Value ?? new List<Game>()).Where(g => g != null).ToList();

        var now = utcNow;
        grace.Observe(allGames, now);

        var visible = allGames
            .Where(g => !g.IsFinished || grace.IsWithinGrace(g, now))
            .Where(g => BbcFixtureSchedule.IsWithinLookAheadWindow(g.StartUtcForOrdering, now))
            .Where(g => g.IsLiveForOrdering
                || grace.IsWithinGrace(g, now)
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
