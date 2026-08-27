using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>
/// Sticky-ordering diff planner for the homepage board. The homepage used to
/// clear and rebuild every league row and match card on every poll (~60s),
/// which re-materialized ~37 card views per refresh on the TV box and
/// reshuffled rows under the user's focus. This planner decides the FINAL
/// row/card ordering so the view model can update in place:
///
/// - A league row keeps its position once shown ("sticky"): while the set of
///   live leagues is unchanged, existing rows never reorder — even when the
///   builder's target tiering disagrees.
/// - Re-tiering (rows reordered to the builder's live-first order) happens
///   ONLY when the delivered live-league set actually changed, and even then
///   the row containing the focused card keeps its position.
/// - New leagues insert at their natural (builder-relative) position but
///   never displace the focused row upward/downward — an insertion that would
///   land at or above the focused row lands directly below it instead.
/// - Card order inside a row is stable across refreshes: existing cards keep
///   their relative order; new cards insert at their target-relative spot;
///   removed cards are dropped.
///
/// Pure and headless: callers own the ObservableCollection mutations.
/// </summary>
public static class HomeBoardDiffer
{
    /// <summary>
    /// Card identity across refreshes — the same fixture on consecutive polls.
    /// Mirrors <see cref="HomePlaybackIntent.SameGame"/>: raw API home/away
    /// names, case-insensitive (display names can change when BBC enrichment
    /// lands; raw names are the stable key).
    /// </summary>
    public static string GameKey(Game game) =>
        $"{(game.Home ?? string.Empty).Trim()}|{(game.Away ?? string.Empty).Trim()}"
            .ToUpperInvariant();

    /// <summary>
    /// Plan the final ordered row list. Returns the row models from
    /// <paramref name="target"/> in the order the UI must show them.
    /// </summary>
    /// <param name="currentOrder">League keys currently on screen, in UI order.</param>
    /// <param name="target">The fresh board in the builder's target (tiered) order.</param>
    /// <param name="previousLiveLeagues">Live-league set of the previously applied board.</param>
    /// <param name="focusedLeague">League of the row holding the focused card, if any.</param>
    public static IReadOnlyList<LeagueRowModel> PlanRowOrder(
        IReadOnlyList<string> currentOrder,
        IReadOnlyList<LeagueRowModel> target,
        IReadOnlyCollection<string> previousLiveLeagues,
        string? focusedLeague)
    {
        var targetByLeague = new Dictionary<string, LeagueRowModel>(StringComparer.OrdinalIgnoreCase);
        var targetIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < target.Count; i++)
        {
            if (targetByLeague.TryAdd(target[i].League, target[i]))
            {
                targetIndex[target[i].League] = i;
            }
        }

        var currentSet = new HashSet<string>(currentOrder, StringComparer.OrdinalIgnoreCase);
        var kept = currentOrder.Where(targetByLeague.ContainsKey).ToList();
        var added = target
            .Select(r => r.League)
            .Where(l => !currentSet.Contains(l))
            .ToList();

        var targetLive = new HashSet<string>(
            target.Where(r => r.HasLiveGames).Select(r => r.League),
            StringComparer.OrdinalIgnoreCase);
        var previousLive = previousLiveLeagues as ISet<string>
            ?? new HashSet<string>(previousLiveLeagues, StringComparer.OrdinalIgnoreCase);
        var liveSetChanged = targetLive.Count != previousLive.Count || !targetLive.All(previousLive.Contains);

        List<string> order;
        if (liveSetChanged)
        {
            // Delivered live-set transition: re-tier to the builder's order,
            // but the focused row keeps its (post-removal) position.
            order = target.Select(r => r.League).ToList();
            if (focusedLeague != null && targetByLeague.ContainsKey(focusedLeague))
            {
                var focusedCurrentIdx = IndexOf(kept, focusedLeague);
                if (focusedCurrentIdx >= 0)
                {
                    order.RemoveAll(l => string.Equals(l, focusedLeague, StringComparison.OrdinalIgnoreCase));
                    order.Insert(Math.Min(focusedCurrentIdx, order.Count), focusedLeague);
                }
            }
        }
        else
        {
            // Sticky: existing rows keep their exact order; new leagues slot
            // in by target-relative position without displacing the focused row.
            order = kept;
            var focusedIdx = focusedLeague == null ? -1 : IndexOf(order, focusedLeague);
            foreach (var league in added)
            {
                var desired = InsertionIndexByTargetOrder(order, targetIndex, targetIndex[league]);
                if (focusedIdx >= 0 && desired <= focusedIdx)
                {
                    desired = focusedIdx + 1;
                }

                order.Insert(desired, league);
                if (desired <= focusedIdx)
                {
                    focusedIdx++;
                }
            }
        }

        return order.Select(l => targetByLeague[l]).ToList();
    }

    /// <summary>
    /// Plan the final ordered card list for one row: existing cards keep their
    /// relative order, new cards insert at their target-relative position,
    /// removed cards are dropped. Returns games from
    /// <paramref name="targetGames"/> in the order the UI must show them.
    /// </summary>
    /// <param name="currentKeys">Card keys (<see cref="GameKey"/>) currently in the row, in UI order.</param>
    /// <param name="targetGames">The row's fresh game list in builder order.</param>
    public static IReadOnlyList<Game> PlanCardOrder(
        IReadOnlyList<string> currentKeys,
        IReadOnlyList<Game> targetGames)
    {
        var targetByKey = new Dictionary<string, Game>(StringComparer.Ordinal);
        var targetIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < targetGames.Count; i++)
        {
            var key = GameKey(targetGames[i]);
            if (targetByKey.TryAdd(key, targetGames[i]))
            {
                targetIndex[key] = i;
            }
        }

        var order = currentKeys.Where(targetByKey.ContainsKey).Distinct().ToList();
        var placed = new HashSet<string>(order, StringComparer.Ordinal);
        foreach (var (key, _) in targetIndex.OrderBy(p => p.Value))
        {
            if (!placed.Add(key))
            {
                continue;
            }

            order.Insert(InsertionIndexByTargetOrder(order, targetIndex, targetIndex[key]), key);
        }

        return order.Select(k => targetByKey[k]).ToList();
    }

    /// <summary>
    /// First position whose element sorts after <paramref name="targetIdx"/>
    /// in the target ordering — i.e. insert before the first later-tiered
    /// element, else append.
    /// </summary>
    private static int InsertionIndexByTargetOrder(
        List<string> order,
        IReadOnlyDictionary<string, int> targetIndex,
        int targetIdx)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (targetIndex.TryGetValue(order[i], out var idx) && idx > targetIdx)
            {
                return i;
            }
        }

        return order.Count;
    }

    private static int IndexOf(List<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
