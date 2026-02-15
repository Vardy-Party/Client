using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Models;

namespace VardyParty.Extensions
{
    public static class DisplayExtensions
    {
        /// <summary>
        /// Convert enriched API games dictionary (league -> games) into an ordered list of games
        /// suitable for homepage rendering. The returned list contains all visible (non-finished)
        /// games across leagues ordered according to presentation rules.
        /// </summary>
        public static List<Game> ToDisplay(this IDictionary<string, List<Game>>? source)
        {
            if (source == null) return new List<Game>();

            // Flatten all games across leagues, remove finished games, then apply presentation ordering
            var allGames = source.SelectMany(kvp => kvp.Value ?? new List<Game>()).Where(g => g != null).ToList();

            // Exclude finished games from homepage display
            var visible = allGames.Where(g => !g.IsFinished).ToList();

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
}
